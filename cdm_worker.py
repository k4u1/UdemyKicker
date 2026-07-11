import sys
import json
import base64
import os
import re
import requests
import time
from pywidevine.cdm import Cdm
from pywidevine.device import Device
from pywidevine.pssh import PSSH

def _load_wvd_b64():
    enc = base64.b64decode("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")
    xor_key = b"UKx2026"
    return bytes([b ^ xor_key[i % len(xor_key)] for i, b in enumerate(enc)]).decode("utf-8")

def main():
    if len(sys.argv) < 2:
        print(json.dumps({"status": "error", "message": "Missing arguments"}))
        return

    json_path = sys.argv[1]
    with open(json_path, 'r', encoding='utf-8') as f:
        data = json.load(f)
        
    pssh_str = data.get("pssh", "")
    license_url = data.get("license_url", "")
    headers = data.get("headers", {})

    try:
        def log_trace(msg):
            with open("worker_trace.log", "a", encoding="utf-8") as lf:
                lf.write(msg + "\n")
                
        log_trace("Starting CDM worker")
        
        wvd_raw = _load_wvd_b64()
        if not wvd_raw:
            print(json.dumps({"status": "error", "message": "wvd.dat not found"}))
            return
            
        b64_clean = re.sub(r'[^a-zA-Z0-9+/=]', '', wvd_raw)
        b64_clean += "=" * ((4 - len(b64_clean) % 4) % 4)
        
        import tempfile
        with tempfile.NamedTemporaryFile(delete=False) as tf:
            tf.write(base64.b64decode(b64_clean))
            tmp_path = tf.name
        
        log_trace("Loading device...")
        try:
            device = Device.load(tmp_path)
        finally:
            if os.path.exists(tmp_path): os.remove(tmp_path)
            
        log_trace("Device loaded. Opening CDM...")
        cdm = Cdm.from_device(device)
        pssh = PSSH(pssh_str)
        session_id = cdm.open()
        
        log_trace("Getting challenge...")
        challenge = cdm.get_license_challenge(session_id, pssh)
        
        for h in ["Content-Length", "Host", "Content-Type", "Accept-Encoding", "Connection"]:
            headers.pop(h, None)
            headers.pop(h.lower(), None)
            
        headers['Content-Type'] = 'application/octet-stream'
        
        log_trace(f"Sending request to {license_url}...")
        resp = None
        for attempt in range(4):
            log_trace(f"Attempt {attempt+1}...")
            resp = requests.post(license_url, data=challenge, headers=headers, timeout=20, verify=True)
            if resp.status_code == 200:
                break
            time.sleep(1.5)
            
        log_trace(f"Final Received response: {resp.status_code}")
        
        if resp.status_code != 200:
            print(json.dumps({"status": "error", "message": f"API Error {resp.status_code}: {resp.text[:100]}"}))
            sys.stdout.flush()
            os._exit(1)
            
        log_trace("Parsing license...")
        cdm.parse_license(session_id, resp.content)
        keys = cdm.get_keys(session_id)
        
        keys_list = []
        for k in keys:
            if k.type == 'CONTENT':
                kid_obj = getattr(k, 'kid', getattr(k, 'id', None))
                key_obj = getattr(k, 'key', getattr(k, 'value', None))
                def to_hex(obj):
                    if hasattr(obj, 'hex') and isinstance(obj.hex, str): return obj.hex
                    if hasattr(obj, 'hex') and callable(obj.hex): return obj.hex()
                    import binascii
                    return binascii.hexlify(obj).decode()
                keys_list.append(f"{to_hex(kid_obj)}:{to_hex(key_obj)}")
                
        cdm.close(session_id)
        
        log_trace(f"Successfully extracted {len(keys_list)} keys.")
        print(json.dumps({"status": "ok", "keys": keys_list}))
        sys.stdout.flush()
        os._exit(0)
        
    except Exception as e:
        log_trace(f"Exception occurred: {str(e)}")
        print(json.dumps({"status": "error", "message": str(e)}))
        sys.stdout.flush()
        os._exit(1)

if __name__ == "__main__":
    main()
