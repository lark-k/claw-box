"""Read-only model smoke against a running board gateway; never print credentials."""
import http.client
import json
from pathlib import Path
import sys

config = json.loads(Path(sys.argv[1]).read_text(encoding="utf-8-sig"))
port = int(sys.argv[2]) if len(sys.argv) > 2 else 18792
request = {
    "model": config["model"],
    "messages": [{"role": "user", "content": "Reply with exactly: QUANTUM_GATEWAY_OK"}],
    "temperature": 0,
    "max_tokens": 128,
    "stream": False,
    "chat_template_kwargs": {"enable_thinking": False},
}
connection = http.client.HTTPConnection("127.0.0.1", port, timeout=90)
try:
    connection.request("POST", "/v1/chat/completions", json.dumps(request).encode(), {
        "Content-Type": "application/json", "Authorization": "Bearer " + config["token"],
    })
    response = connection.getresponse()
    if response.status != 200:
        raise RuntimeError(f"Model gateway returned HTTP {response.status}")
    data = json.loads(response.read())
    if "QUANTUM_GATEWAY_OK" not in data["choices"][0]["message"]["content"]:
        raise RuntimeError("Model did not return the expected test marker")
    print("PASS: box gateway reached configured model")
finally:
    connection.close()
