#!/usr/bin/env python3
"""Scripted OMP typed-RPC server used to test NDJSON framing and session events."""
import json
import sys


def send(obj):
    sys.stdout.write(json.dumps(obj) + "\n")
    sys.stdout.flush()


def response(request_id, command, data=None, error=None):
    payload = {"id": request_id, "type": "response", "command": command}
    if error is None:
        payload["success"] = True
        if data is not None:
            payload["data"] = data
    else:
        payload.update({"success": False, "error": error})
    send(payload)


def main():
    send({
        "type": "ready",
        "protocolVersion": 1,
        "supportedProtocolVersions": [1, 2],
        "maxFrameBytes": 1048576,
        "maxReassembledFrameBytes": 67108864,
    })
    session_id = "fake-session-1"
    session_file = "/tmp/fake-session-1.jsonl"
    for line in sys.stdin:
        line = line.strip()
        if not line:
            continue
        request = json.loads(line)
        command = request.get("type")
        request_id = request.get("id")

        if command == "new_session":
            response(request_id, command, {"cancelled": False})
        elif command == "switch_session":
            session_file = request.get("sessionPath", session_file)
            session_id = "existing-session"
            response(request_id, command, {"cancelled": False})
        elif command == "get_state":
            response(request_id, command, {"sessionId": session_id, "sessionFile": session_file})
        elif command == "prompt":
            response(request_id, command, {"agentInvoked": True})
            if "hang" in request.get("message", ""):
                continue
            send({"type": "message_update", "assistantMessageEvent": {"type": "text_delta", "delta": '{"summary":"done"}'}})
            send({
                "type": "tool_execution_start",
                "toolCallId": "call-1",
                "toolName": "read_file",
                "arguments": {"path": "README.md"},
            })
            send({
                "type": "tool_execution_end",
                "toolCallId": "call-1",
                "isError": False,
                "result": {"content": "hello"},
            })
            send({
                "type": "agent_end",
                "messages": [],
                "isTerminal": True,
            })
        elif command == "abort":
            response(request_id, command, {"cancelled": True})
        else:
            response(request_id, command, error=f"unknown command {command}")


if __name__ == "__main__":
    main()
