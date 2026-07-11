import sys
# Intercept and disable torchvision/torchaudio imports to prevent loading broken C++ binaries at runtime
sys.modules['torchvision'] = None
sys.modules['torchvision.ops'] = None
sys.modules['torchaudio'] = None

import os
import argparse
import glob
import multiprocessing

# Explicit imports to assist PyInstaller packaging for MarianMT models
try:
    import transformers.models.marian.modeling_marian
    import transformers.models.marian.tokenization_marian
    from transformers.models.marian import MarianMTModel, MarianTokenizer
except ImportError:
    pass

def distribute_translation(translated_text, original_cues):
    words = translated_text.split()
    total_eng_len = sum(len(c['clean_text']) for c in original_cues)
    
    if total_eng_len == 0 or len(words) == 0:
        return [c['clean_text'] for c in original_cues]
        
    distributed = []
    current_word_idx = 0
    
    for i, cue in enumerate(original_cues):
        if i == len(original_cues) - 1:
            # Last cue takes all remaining words to prevent truncation
            cue_words = words[current_word_idx:]
        else:
            ratio = len(cue['clean_text']) / total_eng_len
            num_words = max(1, round(ratio * len(words)))
            cue_words = words[current_word_idx : current_word_idx + num_words]
            current_word_idx += num_words
            
        distributed.append(" ".join(cue_words))
        
    return distributed

def translate_subtitle_file(input_path, output_path, model_path, src_lang="eng_Latn", tgt_lang="arb_Arab"):
    if not os.path.exists(input_path):
        sys.stderr.write(f"Input file not found: {input_path}\n")
        return False
        
    try:
        exe_dir = os.path.dirname(os.path.abspath(sys.argv[0]))
        
        # Smart search for local model folders next to the executable/script
        local_hplt = os.path.join(exe_dir, "hplt-model")
        local_nllb = os.path.join(exe_dir, "nllb-model")
        
        target_model = None
        
        if os.path.exists(local_hplt) and os.path.isdir(local_hplt):
            target_model = local_hplt
        elif os.path.exists(local_nllb) and os.path.isdir(local_nllb):
            target_model = local_nllb
        elif model_path and os.path.exists(model_path):
            target_model = model_path
        else:
            if os.path.isdir("hplt-model"):
                target_model = "hplt-model"
            elif os.path.isdir("nllb-model"):
                target_model = "nllb-model"
                
        if not target_model:
            sys.stderr.write("Model directory not found. Please create 'hplt-model' next to the script.\n")
            return False
            
        model_path = target_model

        # Load local Transformers (MarianMT / HPLT) model
        from transformers import AutoTokenizer, AutoModelForSeq2SeqLM
        import torch
        
        sys.stdout.write(f"Loading local MarianMT/HPLT model from: {model_path}...\n")
        tokenizer = AutoTokenizer.from_pretrained(model_path)
        model = AutoModelForSeq2SeqLM.from_pretrained(model_path)
        
        # Tie weights manually to prevent repetition issues in some transformers versions
        try:
            model.lm_head.weight = model.model.shared.weight
        except Exception as tie_ex:
            sys.stderr.write(f"Warning: Failed to manually tie weights: {str(tie_ex)}\n")
        
        # Target GPU if available, else fallback to CPU
        device = "cuda" if torch.cuda.is_available() else "cpu"
        model = model.to(device)
        torch.set_grad_enabled(False)
            
    except Exception as e:
        sys.stderr.write(f"Failed to load local model: {str(e)}\n")
        return False

    with open(input_path, 'r', encoding='utf-8') as f:
        lines = f.readlines()

    # 1. Parse Subtitle Cues cleanly
    cues = []
    current_cue = None
    header_lines = []
    header_ended = False
    
    for line in lines:
        trimmed = line.strip()
        if not header_ended:
            if "-->" in line:
                header_ended = True
            else:
                header_lines.append(line)
                continue
                
        if "-->" in line:
            if current_cue:
                cues.append(current_cue)
            current_cue = {
                'index': None,
                'timing': line,
                'text_lines': []
            }
            # Check if the last collected line in header/previous cue was a digits-only index
            if header_lines and header_lines[-1].strip().isdigit():
                current_cue['index'] = header_lines[-1].strip()
                header_lines.pop()
            elif cues and not cues[-1]['text_lines'] and len(translated_lines) > 0:
                # Fallback check
                pass
        elif current_cue is not None:
            if trimmed == "":
                cues.append(current_cue)
                current_cue = None
            else:
                if trimmed.isdigit() and not current_cue['text_lines']:
                    current_cue['index'] = trimmed
                else:
                    current_cue['text_lines'].append(trimmed)
                    
    if current_cue:
        cues.append(current_cue)

    # Clean empty cues or indices that got mixed in
    for cue in cues:
        cue['clean_text'] = " ".join(cue['text_lines']).strip()

    # 2. Group Cues dynamically into complete sentences
    grouped_cues = []
    current_group = []
    current_word_count = 0
    
    for cue in cues:
        if not cue['clean_text']:
            continue
        current_group.append(cue)
        current_word_count += len(cue['clean_text'].split())
        
        # Check if this cue completes a sentence, or if we reached threshold limits
        ends_with_sentence_end = cue['clean_text'].endswith('.') or cue['clean_text'].endswith('?') or cue['clean_text'].endswith('!')
        if ends_with_sentence_end or len(current_group) >= 3 or current_word_count >= 30:
            grouped_cues.append(current_group)
            current_group = []
            current_word_count = 0
            
    if current_group:
        grouped_cues.append(current_group)

    # 3. Translate Groups with full context and distribute back
    total_groups = len(grouped_cues)
    for idx, group in enumerate(grouped_cues):
        combined_english = " ".join(c['clean_text'] for c in group)
        combined_arabic = translate_text(combined_english, tokenizer, model)
        
        # Distribute the translated sentence back to original subtitle timings proportionally
        distributed_arabic = distribute_translation(combined_arabic, group)
        for cue, ar_text in zip(group, distributed_arabic):
            cue['translated_text'] = ar_text
            
        # Update progress percentage
        pct = int((idx + 1) * 100 / total_groups)
        print(f"PROGRESS: {pct}", flush=True)

    # 4. Generate translated subtitle output
    translated_lines = []
    if header_lines:
        translated_lines.extend(header_lines)
        
    for cue in cues:
        if cue['index']:
            translated_lines.append(f"{cue['index']}\n")
        translated_lines.append(cue['timing'])
        translated_lines.append(f"{cue.get('translated_text', '')}\n\n")

    # Find the start timestamp of the first original subtitle cue
    first_cue_start = None
    for cue in cues:
        if "-->" in cue['timing']:
            parts = cue['timing'].split("-->")
            if len(parts) > 0:
                first_cue_start = parts[0].strip()
                break

    is_vtt = input_path.lower().endswith(".vtt") or (len(lines) > 0 and lines[0].strip().startswith("WEBVTT"))
    
    # Format the credits cue timing
    credits_cue = []
    if is_vtt:
        end_time = first_cue_start if first_cue_start else "00:00:03.000"
        credits_timing = f"00:00:00.000 --> {end_time}"
        credits_cue.append(f"\n{credits_timing}\nتمت الترجمة بواسطة أداة UdemyKicker \nTranslated by UdemyKicker\n\n")
        
        inserted = False
        for idx, line in enumerate(translated_lines):
            if "WEBVTT" in line:
                translated_lines.insert(idx + 1, "".join(credits_cue))
                inserted = True
                break
        if not inserted:
            translated_lines.insert(0, "".join(credits_cue))
    else:
        # SRT format
        end_time = first_cue_start if first_cue_start else "00:00:03,000"
        end_time_srt = end_time.replace(".", ",")
        if len(end_time_srt.split(":")) == 2:
            end_time_srt = "00:" + end_time_srt
            
        credits_timing = f"00:00:00,000 --> {end_time_srt}"
        credits_cue.append(f"1\n{credits_timing}\nتمت الترجمة بواسطة أداة UdemyKicker \nTranslated by UdemyKicker\n\n")
        
        new_lines = []
        new_lines.extend(credits_cue)
        for line in translated_lines:
            trimmed = line.strip()
            if trimmed.isdigit():
                new_lines.append(f"{int(trimmed) + 1}\n")
            else:
                new_lines.append(line)
        translated_lines = new_lines

    with open(output_path, 'w', encoding='utf-8') as f:
        f.writelines(translated_lines)
        
    return True

def translate_text(text, tokenizer, model):
    if not text.strip():
        return text
            
    try:
        import torch
        device = next(model.parameters()).device
        inputs = tokenizer(text, return_tensors="pt").to(device)
        translated = model.generate(**inputs, max_new_tokens=128, num_beams=6)
        translated_result = tokenizer.decode(translated[0], skip_special_tokens=True).strip()
        return translated_result
    except Exception as ex:
        sys.stderr.write(f"MarianMT/HPLT translation error: {str(ex)}\n")
        return text

if __name__ == "__main__":
    parser = argparse.ArgumentParser(description="Offline Subtitle Translator using local model")
    parser.add_argument("--input", required=True, help="Path to input subtitle file (.vtt or .srt)")
    parser.add_argument("--output", required=True, help="Path to save the translated subtitle file")
    parser.add_argument("--model", required=True, help="Path to model folder or file")
    parser.add_argument("--src_lang", default="eng_Latn", help="Source language code (ignored for LLM)")
    parser.add_argument("--tgt_lang", default="arb_Arab", help="Target language code (ignored for LLM)")
    
    args = parser.parse_args()
    
    success = translate_subtitle_file(args.input, args.output, args.model, args.src_lang, args.tgt_lang)
    sys.exit(0 if success else 1)