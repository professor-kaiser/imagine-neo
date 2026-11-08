"""Module entry point: python -m app.main [--pipe-name NAME]."""

import argparse
import logging
import os
from app.ipc.receiver import wait_for_messages


def main() -> None:
    """Initialize logging, start IPC and process incoming UI requests.

    CLI options: --pipe-name (str) overrides the per-user pipe for isolated integration tests.
    Returns None after shutdown; raises OSError if another server already owns the pipe.
    """
    parser = argparse.ArgumentParser(description="Imagine Neo persistent Windows backend")
    parser.add_argument("--pipe-name")
    args = parser.parse_args()
    if args.pipe_name:
        os.environ["PHOTOLAB_PIPE_NAME"] = args.pipe_name
    logging.basicConfig(level=logging.INFO, format="%(asctime)s %(levelname)s %(message)s")
    wait_for_messages()


if __name__ == "__main__":
    main()
