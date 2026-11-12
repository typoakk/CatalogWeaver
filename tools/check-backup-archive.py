"""Validate an archive completely before granting tar access to mounted destinations."""
from pathlib import PurePosixPath
import sys
import tarfile

roots = ("data/config", "data/media", "app/adapters", "app/encoders")
with tarfile.open(sys.argv[1], "r:gz") as archive:
    for member in archive:
        name = member.name.rstrip("/")
        path = PurePosixPath(name)
        if path.is_absolute() or ".." in path.parts or "\\" in name:
            sys.exit("Unsafe archive member path.")
        if not any(name == root or name.startswith(root + "/") for root in roots):
            sys.exit("Unexpected backup archive destination.")
        if not (member.isfile() or member.isdir()):
            sys.exit("Backup archives may contain only regular files and directories.")
