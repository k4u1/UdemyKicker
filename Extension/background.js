import "./protobuf.min.js";
import "./license_protocol.js";
import "./forge.min.js";

import {
    base64toUint8Array,
    uint8ArrayToHex,
    SettingsManager,
    AsyncLocalStorage
} from "./util.js";

const { SignedMessage, LicenseRequest } = protobuf.roots.default.license_protocol;

let manifests = new Map();
let requests = new Map();
let licenseUrls = new Map(); // tabId -> licenseUrl
let logs = [];

// Capture License URLs from outgoing POST requests
chrome.webRequest.onBeforeRequest.addListener(
    function (details) {
        if (details.method === "POST" && (details.url.includes("license") || details.url.includes("widevine") || details.url.includes("drm"))) {
            licenseUrls.set(details.tabId, details.url);
        }
    },
    { urls: ["<all_urls>"] }
);

chrome.webRequest.onBeforeSendHeaders.addListener(
    function (details) {
        const headers = details.requestHeaders.reduce((acc, item) => {
            const name = item.name.toLowerCase();
            // Keep everything except browser/extension infra headers
            if (!name.startsWith('sec-ch-ua') &&
                !name.startsWith('sec-fetch') &&
                name !== 'host' &&
                name !== 'connection') {
                acc[item.name] = item.value;
            }
            return acc;
        }, {});

        requests.set(details.url, headers);
    },
    { urls: ["<all_urls>"] },
    ['requestHeaders', chrome.webRequest.OnSendHeadersOptions.EXTRA_HEADERS].filter(Boolean)
);

async function captureMetadata(body, sendResponse, tab_url, tabId) {
    let pssh = null;
    try {
        const signed_message = SignedMessage.decode(base64toUint8Array(body));
        const license_request = LicenseRequest.decode(signed_message.msg);
        const pssh_data = license_request.contentId.widevinePsshData.psshData[0];

        if (pssh_data) {
            // Convert to PSSH Box B64
            const dataLength = pssh_data.length;
            const totalLength = dataLength + 32;
            const pssh_box = new Uint8Array(totalLength);
            const view = new DataView(pssh_box.buffer);
            view.setUint32(0, totalLength);
            pssh_box.set([0x70, 0x73, 0x73, 0x68], 4);
            // System ID
            pssh_box.set([0xed, 0xef, 0x8b, 0xa9, 0x79, 0xd6, 0x4a, 0xce, 0xa3, 0xc8, 0x27, 0xdc, 0xd5, 0x1d, 0x21, 0xed], 12);
            view.setUint32(28, dataLength);
            pssh_box.set(pssh_data, 32);

            pssh = btoa(String.fromCharCode.apply(null, pssh_box));
        }
    } catch (e) {
        // Not a standard Widevine challenge?
    }

    if (!pssh) {
        sendResponse(body);
        return;
    }

    const license_url = licenseUrls.get(tabId) || "";
    const license_headers = requests.get(license_url) || {};

    const log = {
        type: "METADATA",
        pssh_data: pssh,
        license_url: license_url,
        license_headers: license_headers,
        url: tab_url,
        tabId: tabId,
        timestamp: Math.floor(Date.now() / 1000),
        manifests: manifests.get(tab_url) || []
    };

    logs.push(log);
    await AsyncLocalStorage.setStorage({ [pssh]: log });

    // Send raw data directly to the page so C# can capture it
    chrome.tabs.sendMessage(tabId, { type: "CMD_READY", cmd: JSON.stringify(log) }).catch(() => { });

    sendResponse(body); // Return original challenge so playback continues
}

chrome.runtime.onMessage.addListener((message, sender, sendResponse) => {
    (async () => {
        const tab_url = sender.tab ? sender.tab.url : null;
        const tabId = sender.tab ? sender.tab.id : null;

        switch (message.type) {
            case "REQUEST":
                if (message.body) {
                    await captureMetadata(message.body, sendResponse, tab_url, tabId);
                }
                break;

            case "RESPONSE":
                // We are passive now, don't need to parse license
                sendResponse(message.body);
                break;

            case "GET_LOGS":
                sendResponse(logs);
                break;

            case "CLEAR":
                logs = [];
                manifests.clear();
                licenseUrls.clear();
                break;

            case "MANIFEST":
                const parsed = JSON.parse(message.body);
                const element = {
                    type: parsed.type,
                    url: parsed.url,
                    headers: requests.has(parsed.url) ? requests.get(parsed.url) : {},
                };

                if (!manifests.has(tab_url)) {
                    manifests.set(tab_url, [element]);
                } else {
                    let elements = manifests.get(tab_url);
                    if (!elements.some(e => e.url === parsed.url)) {
                        elements.push(element);
                        manifests.set(tab_url, elements);
                    }
                }
                sendResponse();
                break;
        }
    })();
    return true;
});

