"""Check actual deployed mounts; current compose.yaml may differ from the running deployment."""
import json
import sys

sys.dont_write_bytecode = True
from backup_contract import check_containers

try:
    if len(sys.argv) != 2:
        raise ValueError("Supply the resolved Compose project name.")
    check_containers(json.load(sys.stdin), sys.argv[1])
except (ValueError, TypeError, KeyError, AttributeError):
    sys.exit("Deployed storage differs from the standard managed-volume layout; backup refused.")
