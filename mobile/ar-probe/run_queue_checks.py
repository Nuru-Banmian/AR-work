"""Real Go + Unity queue acceptance; isolated data, deliberate lost first receipt.

python mobile/ar-probe/run_queue_checks.py --unity <Unity.exe> --go <go.exe>
"""
import argparse
import json
import os
from pathlib import Path
import socket
import subprocess
import tempfile
import threading
import time
import urllib.request
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--unity', required=True)
    parser.add_argument('--go', required=True)
    args = parser.parse_args()
    root = Path(__file__).resolve().parents[2]
    output = root / '.local' / 'issue15-checks'
    output.mkdir(parents=True, exist_ok=True)
    # Fail before touching anything if these dedicated test ports are occupied.
    for port in (18095, 18096):
        with socket.socket() as probe:
            probe.bind(('127.0.0.1', port))
    with tempfile.TemporaryDirectory(prefix='arwork-queue-') as temporary:
        temporary = Path(temporary)
        binary = temporary / 'backend.exe'
        subprocess.run([args.go, 'build', '-o', str(binary), '.'], cwd=root / 'backend', check=True)
        environment = dict(os.environ, ARWORK_ADDR='127.0.0.1:18095', ARWORK_DATA=str(temporary / 'issues.json'))
        process = None
        log = (output / 'backend.log').open('w')

        def start_backend():
            nonlocal process
            process = subprocess.Popen([str(binary)], env=environment, stdout=log, stderr=log)
            for _ in range(100):
                try:
                    with urllib.request.urlopen('http://127.0.0.1:18095/health', timeout=1):
                        return
                except OSError:
                    time.sleep(.1)
            raise RuntimeError('Temporary backend did not start')

        class Proxy(BaseHTTPRequestHandler):
            dropped = False

            def do_POST(self):
                body = self.rfile.read(int(self.headers['Content-Length']))
                request = urllib.request.Request('http://127.0.0.1:18095' + self.path, data=body,
                    headers={'Content-Type': 'application/json', 'Idempotency-Key': self.headers['Idempotency-Key']})
                with urllib.request.urlopen(request, timeout=10) as response:
                    data, status = response.read(), response.status
                with (output / 'requests.jsonl').open('a') as evidence:
                    evidence.write(json.dumps({'key': self.headers['Idempotency-Key'], 'body': body.decode(), 'status': status}) + '\n')
                if not Proxy.dropped:
                    Proxy.dropped = True
                    process.terminate()
                    process.wait(timeout=10)
                    start_backend()  # persisted request must survive backend restart, too
                    self.connection.shutdown(socket.SHUT_RDWR)
                    self.connection.close()
                    return
                self.send_response(status)
                self.end_headers()
                self.wfile.write(data)

        proxy = None
        try:
            start_backend()
            proxy = ThreadingHTTPServer(('127.0.0.1', 18096), Proxy)
            threading.Thread(target=proxy.serve_forever, daemon=True).start()
            result = subprocess.run([args.unity, '-batchmode', '-nographics', '-quit', '-projectPath',
                str(root / 'mobile' / 'ar-probe'), '-executeMethod', 'ReportQueueChecks.Run',
                '-logFile', str(output / 'unity.log')], timeout=300)
            contents = (output / 'unity.log').read_text(encoding='utf-8', errors='replace')
            if result.returncode or 'ISSUE15_QUEUE_CHECKS_PASSED' not in contents:
                raise RuntimeError(f'Queue checks failed; see {output / "unity.log"}')
            print('Queue acceptance passed. Evidence:', output)
        finally:
            if proxy:
                proxy.shutdown()
                proxy.server_close()
            if process:
                process.terminate()
                process.wait(timeout=10)
            log.close()


if __name__ == '__main__':
    main()
