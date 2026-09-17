#!/usr/bin/env python3
"""Minimal scripted NDJSON RPC server used only to test NdjsonRpcTransport framing,
correlation, and notification delivery. Not a real OMP implementation."""
import json
import sys


def send(obj):
    sys.stdout.write(json.dumps(obj) + "\n")
    sys.stdout.flush()


def main():
    for line in sys.stdin:
        line = line.strip()
        if not line:
            continue
        request = json.loads(line)
        method = request.get("method")
        request_id = request.get("id")
        params = request.get("params") or {}

        if method == "session.create":
            send({"id": request_id, "result": {"sessionId": "fake-session-1"}})
        elif method == "session.resume":
            send({"id": request_id, "result": {"role": "task"}})
        elif method == "run":
            session_id = params.get("sessionId")
            send({"id": request_id, "result": {"accepted": True}})
            send({"method": "event", "params": {
                "sessionId": session_id, "type": "message", "text": "starting work",
                "timestamp": "2024-01-01T00:00:00Z",
            }})
            send({"method": "event", "params": {
                "sessionId": session_id, "type": "toolCall", "toolCallId": "call-1",
                "toolName": "read_file", "arguments": {"path": "README.md"},
                "timestamp": "2024-01-01T00:00:01Z",
            }})
            send({"method": "event", "params": {
                "sessionId": session_id, "type": "toolResult", "toolCallId": "call-1",
                "isError": False, "result": {"content": "hello"},
                "timestamp": "2024-01-01T00:00:02Z",
            }})
            send({"method": "event", "params": {
                "sessionId": session_id, "type": "completed",
                "result": {"summary": "done"},
                "timestamp": "2024-01-01T00:00:03Z",
            }})
        elif method == "run-error":
            session_id = params.get("sessionId")
            send({"id": request_id, "result": {"accepted": True}})
            send({"method": "event", "params": {
                "sessionId": session_id, "type": "error", "message": "boom", "cancelled": False,
                "timestamp": "2024-01-01T00:00:01Z",
            }})
        elif method == "cancel":
            send({"id": request_id, "result": {"cancelled": True}})
        elif method == "error-request":
            send({"id": request_id, "error": {"message": "requested failure"}})
        else:
            send({"id": request_id, "error": {"message": f"unknown method {method}"}})


if __name__ == "__main__":
    main()
