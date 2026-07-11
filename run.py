#!/usr/bin/env python3
import argparse
import os
import socket
import shutil
import signal
import subprocess
import sys
import time
import urllib.error
import urllib.request
from pathlib import Path


ROOT = Path(__file__).resolve().parent
FRONTEND = ROOT / "frontend"
ENV_FILE = ROOT / ".env"
DEFAULT_ENV = {
    "MYSQL_DATABASE": "nodeck",
    "MYSQL_USER": "nodeck",
    "MYSQL_PASSWORD": "nodeck",
    "MYSQL_ROOT_PASSWORD": "root",
    "BACKEND_HTTP_PORT": "8080",
}


def main() -> int:
    parser = argparse.ArgumentParser(description="Run the Nodeck PR #5 verification stack.")
    parser.add_argument(
        "--reset-db",
        action="store_true",
        help="Remove volumes/mysql_data before starting so MySQL re-runs database/init scripts.",
    )
    args = parser.parse_args()

    ensure_command("docker", "Install Docker and make sure the daemon is running.")
    ensure_command("node", "Install Node.js.")
    ensure_command("npm", "Install npm.")

    compose = get_compose_command()
    write_env_file()

    if args.reset_db:
        reset_mysql_data()

    run([*compose, "up", "--build", "-d"], cwd=ROOT)
    wait_for_url("http://localhost:8080/health", "backend health", compose)
    wait_for_url("http://localhost:8080/health/mysql", "MySQL health", compose)

    if not (FRONTEND / "node_modules").exists():
        run(["npm", "install"], cwd=FRONTEND)

    print()
    print("Backend:  http://localhost:8080")
    print("Frontend: http://localhost:5173")
    print()
    print("Open:")
    print("  http://localhost:5173/cards/new")
    print("  http://localhost:5173/cards")
    print("  http://localhost:5173/gallery")
    print()
    print("Press Ctrl+C to stop the frontend dev server.")

    return run_foreground(["npm", "run", "dev", "--", "--host", "0.0.0.0"], cwd=FRONTEND)


def ensure_command(command: str, message: str) -> None:
    if shutil.which(command) is None:
        raise SystemExit(f"Missing `{command}`. {message}")


def get_compose_command() -> list[str]:
    if command_succeeds(["docker", "compose", "version"]):
        return ["docker", "compose"]

    if shutil.which("docker-compose") is not None:
        return ["docker-compose"]

    raise SystemExit("Missing Docker Compose. Install the Docker Compose plugin or docker-compose.")


def command_succeeds(command: list[str]) -> bool:
    return subprocess.run(
        command,
        cwd=ROOT,
        stdout=subprocess.DEVNULL,
        stderr=subprocess.DEVNULL,
        check=False,
    ).returncode == 0


def write_env_file() -> None:
    existing = read_env_file()
    merged = {**DEFAULT_ENV, **existing}
    content = "\n".join(f"{key}={value}" for key, value in merged.items()) + "\n"

    if ENV_FILE.exists() and ENV_FILE.read_text() == content:
        return

    ENV_FILE.write_text(content)
    print(f"Wrote {ENV_FILE.relative_to(ROOT)}")


def read_env_file() -> dict[str, str]:
    if not ENV_FILE.exists():
        return {}

    values: dict[str, str] = {}

    for line in ENV_FILE.read_text().splitlines():
        stripped = line.strip()

        if not stripped or stripped.startswith("#") or "=" not in stripped:
            continue

        key, value = stripped.split("=", 1)
        values[key.strip()] = value.strip()

    return values


def reset_mysql_data() -> None:
    mysql_data = ROOT / "volumes" / "mysql_data"

    if mysql_data.exists():
        shutil.rmtree(mysql_data)
        print("Removed volumes/mysql_data")


def wait_for_url(
    url: str,
    label: str,
    compose: list[str],
    timeout_seconds: int = 90,
) -> None:
    deadline = time.time() + timeout_seconds
    last_error: Exception | None = None

    print(f"Waiting for {label}...")

    while time.time() < deadline:
        try:
            with urllib.request.urlopen(url, timeout=3) as response:
                if 200 <= response.status < 300:
                    print(f"{label} is ready.")
                    return
        except (
            ConnectionError,
            OSError,
            TimeoutError,
            socket.timeout,
            urllib.error.URLError,
        ) as error:
            last_error = error

        time.sleep(2)

    print()
    print(f"{label} did not become ready. Container status:")
    subprocess.run([*compose, "ps"], cwd=ROOT, check=False)
    print()
    print("Backend logs:")
    subprocess.run([*compose, "logs", "--tail", "80", "backend"], cwd=ROOT, check=False)

    raise SystemExit(f"Timed out waiting for {label}: {last_error}")


def run(command: list[str], cwd: Path) -> None:
    print(f"$ {' '.join(command)}")
    subprocess.run(command, cwd=cwd, check=True)


def run_foreground(command: list[str], cwd: Path) -> int:
    print(f"$ {' '.join(command)}")
    process = subprocess.Popen(command, cwd=cwd)

    def stop_process(signum: int, _frame: object) -> None:
        process.send_signal(signum)

    previous_sigint = signal.signal(signal.SIGINT, stop_process)
    previous_sigterm = signal.signal(signal.SIGTERM, stop_process)

    try:
        return process.wait()
    finally:
        signal.signal(signal.SIGINT, previous_sigint)
        signal.signal(signal.SIGTERM, previous_sigterm)


if __name__ == "__main__":
    try:
        raise SystemExit(main())
    except subprocess.CalledProcessError as error:
        raise SystemExit(error.returncode)
