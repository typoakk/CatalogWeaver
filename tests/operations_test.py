"""Safety checks for backup helpers. Run with: python3 tests/operations_test.py"""
import copy
import gzip
import io
import json
import os
from pathlib import Path
import subprocess
import sys
import tarfile
import tempfile
import unittest

ROOT = Path(__file__).resolve().parents[1]
PROJECT = "operations_test"
VOLUMES = ("configuration", "state", "adapters", "encoders", "media")
MOUNTS = {
    "bootstrap": {"/data/config": ("configuration", False)},
    "state": {"/var/lib/postgresql": ("state", False), "/run/catalogweaver": ("configuration", True)},
    "migrate": {"/data/config": ("configuration", True)},
    "importer": {
        "/data/config": ("configuration", True),
        "/app/adapters": ("adapters", False),
        "/app/encoders": ("encoders", False),
        "/data/media": ("media", False),
    },
}


def standard_config():
    return {
        "name": PROJECT,
        "volumes": {name: {"name": f"{PROJECT}_{name}"} for name in VOLUMES},
        "services": {
            service: {
                "volumes": [
                    {"type": "volume", "source": source, "target": target, "read_only": read_only}
                    for target, (source, read_only) in mounts.items()
                ]
            }
            for service, mounts in MOUNTS.items()
        },
    }


def deployed_containers(services=("importer", "state")):
    return [
        {
            "Config": {"Labels": {"com.docker.compose.project": PROJECT, "com.docker.compose.service": service}},
            "State": {"Status": "running" if service in ("importer", "state") else "exited"},
            "Mounts": [
                {"Type": "volume", "Name": f"{PROJECT}_{source}", "Driver": "local",
                 "Destination": target, "RW": not read_only}
                for target, (source, read_only) in MOUNTS[service].items()
            ],
        }
        for service in services
    ]


def run_helper(name, value=None, args=()):
    environment = dict(os.environ, PYTHONDONTWRITEBYTECODE="1")
    return subprocess.run(
        [sys.executable, str(ROOT / "tools" / name), *map(str, args)],
        input=None if value is None else json.dumps(value),
        capture_output=True, text=True, timeout=10, env=environment,
    )


class ComposeLayoutTests(unittest.TestCase):
    def assert_refused(self, config):
        result = run_helper("check-backup-layout.py", config)
        self.assertNotEqual(result.returncode, 0)

    def test_standard_layout_is_accepted_and_can_return_resolved_project(self):
        result = run_helper("check-backup-layout.py", standard_config())
        self.assertEqual(result.returncode, 0, result.stderr)
        self.assertEqual(result.stdout, "")
        result = run_helper("check-backup-layout.py", standard_config(), ("--print-project",))
        self.assertEqual(result.returncode, 0, result.stderr)
        self.assertEqual(result.stdout.strip(), PROJECT)

    def test_bind_mount_is_refused_even_with_a_standard_volume_source_name(self):
        config = standard_config()
        config["services"]["importer"]["volumes"][-1]["type"] = "bind"
        self.assert_refused(config)

    def test_external_shared_host_backed_and_plugin_volumes_are_refused(self):
        for replacement in (
            {"external": True, "name": f"{PROJECT}_media"},
            {"name": "another_project_media"},
            {"name": f"{PROJECT}_media", "driver_opts": {"type": "none", "device": "/srv/data", "o": "bind"}},
            {"name": f"{PROJECT}_media", "driver": "remote-plugin"},
        ):
            with self.subTest(replacement=replacement):
                config = standard_config()
                config["volumes"]["media"] = replacement
                self.assert_refused(config)

    def test_missing_or_extra_volume_definitions_are_refused(self):
        config = standard_config()
        del config["volumes"]["media"]
        self.assert_refused(config)
        config = standard_config()
        config["volumes"]["other"] = {"name": f"{PROJECT}_other"}
        self.assert_refused(config)

    def test_missing_moved_crosswired_duplicate_or_writable_secret_mount_is_refused(self):
        changes = ("missing", "moved", "crosswired", "duplicate", "secret_writable")
        for change in changes:
            with self.subTest(change=change):
                config = standard_config()
                mounts = config["services"]["importer"]["volumes"]
                if change == "missing":
                    mounts.pop()
                elif change == "moved":
                    mounts[-1]["target"] = "/elsewhere"
                elif change == "crosswired":
                    mounts[-1]["source"] = "adapters"
                elif change == "duplicate":
                    mounts[-1] = copy.deepcopy(mounts[-2])
                else:
                    mounts[0]["read_only"] = False
                self.assert_refused(config)

    def test_missing_or_unknown_services_are_refused(self):
        config = standard_config()
        del config["services"]["importer"]
        self.assert_refused(config)
        config = standard_config()
        config["services"]["other"] = {"volumes": []}
        self.assert_refused(config)


class DeployedMountTests(unittest.TestCase):
    def assert_refused(self, containers):
        result = run_helper("check-backup-containers.py", containers, (PROJECT,))
        self.assertNotEqual(result.returncode, 0)

    def test_actual_running_services_are_required_but_completed_setup_containers_are_optional(self):
        for services in (("importer", "state"), tuple(MOUNTS)):
            with self.subTest(services=services):
                result = run_helper("check-backup-containers.py", deployed_containers(services), (PROJECT,))
                self.assertEqual(result.returncode, 0, result.stderr)

    def test_bind_deployment_is_refused_even_when_current_compose_config_is_standard(self):
        current = run_helper("check-backup-layout.py", standard_config())
        self.assertEqual(current.returncode, 0, current.stderr)
        deployed = deployed_containers()
        deployed[0]["Mounts"][-1] = {
            "Type": "bind", "Source": "/existing/live/media", "Destination": "/data/media", "RW": True
        }
        self.assert_refused(deployed)

    def test_missing_service_duplicate_service_or_other_project_is_refused(self):
        self.assert_refused(deployed_containers(("importer",)))
        self.assert_refused(deployed_containers(("importer", "state", "importer")))
        deployed = deployed_containers()
        deployed[1]["Config"]["Labels"]["com.docker.compose.project"] = "another_project"
        self.assert_refused(deployed)

    def test_missing_moved_shared_crosswired_and_wrong_permissions_are_refused(self):
        for change in ("missing", "moved", "shared", "crosswired", "permissions", "extra", "driver"):
            with self.subTest(change=change):
                deployed = deployed_containers()
                mounts = deployed[0]["Mounts"]
                if change == "missing":
                    mounts.pop()
                elif change == "moved":
                    mounts[-1]["Destination"] = "/elsewhere"
                elif change == "shared":
                    mounts[-1]["Name"] = "another_project_media"
                elif change == "crosswired":
                    mounts[-1]["Name"] = f"{PROJECT}_adapters"
                elif change == "permissions":
                    mounts[0]["RW"] = True
                elif change == "extra":
                    mounts.append({"Type": "bind", "Source": "/host", "Destination": "/host", "RW": True})
                else:
                    mounts[-1]["Driver"] = "remote-plugin"
                self.assert_refused(deployed)


class ArchiveSafetyTests(unittest.TestCase):
    def check_archive(self, members):
        with tempfile.TemporaryDirectory(prefix="catalogweaver-archive-test-") as directory:
            path = Path(directory) / "volumes.tar.gz"
            with tarfile.open(path, "w:gz") as archive:
                for name, kind, link in members:
                    member = tarfile.TarInfo(name)
                    member.type = kind
                    member.linkname = link
                    data = b"test-fixture" if kind == tarfile.REGTYPE else b""
                    member.size = len(data)
                    archive.addfile(member, io.BytesIO(data) if data else None)
            return run_helper("check-backup-archive.py", args=(path,))

    def test_regular_files_and_directories_inside_each_root_are_accepted(self):
        members = []
        for root in ("data/config", "data/media", "app/adapters", "app/encoders"):
            members.extend(((root + "/", tarfile.DIRTYPE, ""), (root + "/example", tarfile.REGTYPE, "")))
        result = self.check_archive(members)
        self.assertEqual(result.returncode, 0, result.stderr)

    def test_traversal_absolute_backslash_and_unexpected_roots_are_refused(self):
        for name in ("/etc/passwd", "../outside", "data/media/../outside", "data/media/nested/../../outside",
                     "data/media\\outside", "data/media-other/file", "app/other/file"):
            with self.subTest(name=name):
                result = self.check_archive(((name, tarfile.REGTYPE, ""),))
                self.assertNotEqual(result.returncode, 0)

    def test_symlinks_hardlinks_and_special_files_are_refused(self):
        for kind in (tarfile.SYMTYPE, tarfile.LNKTYPE, tarfile.FIFOTYPE, tarfile.CHRTYPE, tarfile.BLKTYPE):
            with self.subTest(kind=kind):
                result = self.check_archive((("data/media/link", kind, "/outside"),))
                self.assertNotEqual(result.returncode, 0)

    def test_invalid_and_truncated_archives_are_refused(self):
        for data in (gzip.compress(b"not a tar archive"),
                     gzip.compress(tarfile.TarInfo("data/media/oversized").tobuf()[:100])):
            with self.subTest(size=len(data)), tempfile.TemporaryDirectory(prefix="catalogweaver-archive-test-") as directory:
                path = Path(directory) / "volumes.tar.gz"
                path.write_bytes(data)
                result = run_helper("check-backup-archive.py", args=(path,))
                self.assertNotEqual(result.returncode, 0)


if __name__ == "__main__":
    unittest.main(verbosity=2)
