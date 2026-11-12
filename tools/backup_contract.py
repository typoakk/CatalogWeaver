"""The supported Compose storage layout for a consistent managed-volume backup."""
import re

VOLUMES = {"configuration", "state", "adapters", "encoders", "media"}
MOUNTS = {
    "bootstrap": {"/data/config": ("configuration", False)},
    "state": {
        "/var/lib/postgresql": ("state", False),
        "/run/catalogweaver": ("configuration", True),
    },
    "migrate": {"/data/config": ("configuration", True)},
    "importer": {
        "/data/config": ("configuration", True),
        "/app/adapters": ("adapters", False),
        "/app/encoders": ("encoders", False),
        "/data/media": ("media", False),
    },
}


def valid_project(value):
    return isinstance(value, str) and re.fullmatch(r"[a-z0-9][a-z0-9_-]*", value) is not None


def check_config(config):
    if not isinstance(config, dict) or not valid_project(config.get("name")):
        raise ValueError("Invalid Compose project identity.")
    project = config["name"]
    volumes = config.get("volumes", {})
    if set(volumes) != VOLUMES:
        raise ValueError("Backup requires the standard five managed volumes.")
    for name, definition in volumes.items():
        if not isinstance(definition, dict) or definition.get("external") or definition.get("name") != f"{project}_{name}" \
                or definition.get("driver_opts") or definition.get("driver", "local") != "local":
            raise ValueError("External, shared or host-backed volumes are not supported by this procedure.")
    services = config.get("services", {})
    if set(services) != set(MOUNTS):
        raise ValueError("Backup requires the standard four Compose services.")
    for name, service in services.items():
        mounts = service.get("volumes", [])
        if len(mounts) != len(MOUNTS[name]):
            raise ValueError(f"Service {name} must mount every standard storage destination exactly once.")
        seen = set()
        for mount in mounts:
            target = mount.get("target")
            expected = MOUNTS[name].get(target)
            if expected is None or target in seen or mount.get("type") != "volume" \
                    or mount.get("source") != expected[0] or bool(mount.get("read_only", False)) != expected[1]:
                raise ValueError(f"Service {name} has a missing, moved or unsafe storage mount.")
            seen.add(target)
    return project


def check_containers(containers, project):
    if not valid_project(project) or not isinstance(containers, list):
        raise ValueError("Invalid deployed Compose storage description.")
    found = set()
    for container in containers:
        labels = container.get("Config", {}).get("Labels") or {}
        service = labels.get("com.docker.compose.service")
        if labels.get("com.docker.compose.project") != project or service not in MOUNTS:
            raise ValueError("The deployed containers do not belong to the expected Compose services.")
        if service in found:
            raise ValueError("Backup requires one container per standard service; stop duplicate deployments first.")
        found.add(service)
        mounts = container.get("Mounts", [])
        if len(mounts) != len(MOUNTS[service]):
            raise ValueError(f"Deployed {service} does not mount the complete supported storage layout.")
        seen = set()
        for mount in mounts:
            target = mount.get("Destination")
            expected = MOUNTS[service].get(target)
            if expected is None or target in seen or mount.get("Type") != "volume" \
                    or mount.get("Name") != f"{project}_{expected[0]}" \
                    or mount.get("Driver", "local") != "local" or bool(mount.get("RW", True)) == expected[1]:
                raise ValueError(f"Deployed {service} uses bind, shared, moved or unsafe storage mounts.")
            seen.add(target)
    if not {"importer", "state"}.issubset(found):
        raise ValueError("Create both importer and state containers before backing up.")
