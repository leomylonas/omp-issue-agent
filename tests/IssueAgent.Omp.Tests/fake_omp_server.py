#!/usr/bin/env python3
"""Scripted OMP typed-RPC server used to test NDJSON framing and session events."""
import json
import os
import sys
import time


def send(obj):
    sys.stdout.write(json.dumps(obj) + "\n")
    sys.stdout.flush()


def response(request_id, command, data=None, error=None, error_code=None):
    payload = {"id": request_id, "type": "response", "command": command}
    if error is None:
        payload["success"] = True
        if data is not None:
            payload["data"] = data
    else:
        payload.update({"success": False, "error": error})
        if error_code is not None:
            payload["errorCode"] = error_code
    send(payload)

def emit_standard_error():
    byte_count = int(os.environ.get("OMP_STDERR_BYTES", "0"))
    if byte_count > 0:
        payload = (b"stderr-secret-value\n" * ((byte_count // 20) + 1))[:byte_count]
        sys.stderr.buffer.write(payload)
        sys.stderr.buffer.flush()




def main():
    ready_delay_ms = int(os.environ.get("OMP_READY_DELAY_MS", "0"))
    if ready_delay_ms > 0:
        time.sleep(ready_delay_ms / 1000)
    send({
        "type": "ready",
        "protocolVersion": 1,
        "supportedProtocolVersions": [1, 2],
        "maxFrameBytes": 1048576,
        "maxReassembledFrameBytes": 67108864,
    })
    argument_log = os.environ.get("OMP_ARGUMENT_LOG")
    if argument_log:
        with open(argument_log, "a", encoding="utf-8") as log:
            log.write(" ".join(sys.argv[1:]) + "\n")
    startup_model = None
    if "--model" in sys.argv:
        startup_model = sys.argv[sys.argv.index("--model") + 1]
    model = {"provider": "configured", "id": startup_model or "default"}
    command_log = os.environ.get("OMP_COMMAND_LOG")
    session_id = "fake-session-1"
    session_file = "/tmp/fake-session-1.jsonl"
    hang_abort = False
    for line in sys.stdin:
        line = line.strip()
        if not line:
            continue
        request = json.loads(line)
        command = request.get("type")
        request_id = request.get("id")
        if command_log:
            with open(command_log, "a", encoding="utf-8") as log:
                log.write(command + " " + json.dumps(request) + "\n")
        if command in os.environ.get("OMP_HANG_COMMANDS", "").split(","):
            continue

        if command == "new_session":
            emit_standard_error()
            if os.environ.get("OMP_EXIT_AFTER_STDERR") == "1":
                return
            if error_code := os.environ.get("OMP_NEW_SESSION_ERROR_CODE"):
                response(request_id, command, error="startup dependency unavailable", error_code=error_code)
                continue
            response(request_id, command, {"cancelled": False})
        elif command == "set_model":
            model = {"provider": request.get("provider"), "id": request.get("modelId")}
            response(request_id, command, model)
        elif command == "switch_session":
            session_file = request.get("sessionPath", session_file)
            session_id = (
                "mismatched-session" if "mismatch" in session_file
                else "fake-session-1" if "fake-session-1" in session_file
                else "existing-session"
            )
            model = {"provider": "configured", "id": "plan"}
            response(request_id, command, {"cancelled": False})
        elif command == "get_state":
            response(request_id, command, {"sessionId": session_id, "sessionFile": session_file, "model": model})
        elif command == "prompt":
            if "prompt dispatch hang" in request.get("message", ""):
                continue
            response(request_id, command, {"agentInvoked": True})
            hang_abort = "abort timeout" in request.get("message", "")
            if "hang" in request.get("message", ""):
                continue
            if "malformed frame" in request.get("message", ""):
                print("{not-json", flush=True)
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
            if not hang_abort:
                response(request_id, command, {"cancelled": True})
        else:
            response(request_id, command, error=f"unknown command {command}")


if __name__ == "__main__":
    main()
