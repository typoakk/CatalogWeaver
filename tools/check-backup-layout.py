"""Refuse incomplete/bind/shared storage before backup or restore can touch data."""
import json
import sys

sys.dont_write_bytecode = True
from backup_contract import check_config

try:
    project = check_config(json.load(sys.stdin))
except (ValueError, TypeError, KeyError, AttributeError):
    sys.exit("Unsupported Compose storage layout: use the standard services and managed volumes.")
if "--print-project" in sys.argv[1:]:
    print(project)
