#!/usr/bin/env python3
"""Build deterministic, self-contained bdeyes preview archives."""

from __future__ import annotations

import argparse
import gzip
import hashlib
import json
import os
from pathlib import Path
import re
import shutil
import stat
import subprocess
import tarfile
import tempfile
import xml.etree.ElementTree as ElementTree
import zipfile


REPOSITORY_ROOT = Path(__file__).resolve().parent.parent
PROJECT = REPOSITORY_ROOT / "src" / "Bdeyes" / "Bdeyes.csproj"
RUNTIMES = {
    "win-x64": ("Bdeyes.exe", ".zip"),
    "linux-x64": ("Bdeyes", ".tar.gz"),
}
NOTICE_NAME = re.compile(r"license|notice|copying", re.IGNORECASE)
INVALID_ARCHIVE_NAME = re.compile(r"[\\/:*?\"<>|]")


def run(arguments: list[str], *, capture: bool = False) -> subprocess.CompletedProcess[str]:
    return subprocess.run(
        arguments,
        cwd=REPOSITORY_ROOT,
        check=True,
        text=True,
        capture_output=capture,
    )


def project_version() -> str:
    result = run(
        ["dotnet", "msbuild", str(PROJECT), "-nologo", "-getProperty:Version"],
        capture=True,
    )
    version = result.stdout.strip()
    if not version:
        raise RuntimeError("Could not read the bdeyes project version.")
    return version


def xml_name(element: ElementTree.Element) -> str:
    return element.tag.rsplit("}", 1)[-1]


def first_child(
    element: ElementTree.Element | None,
    name: str,
) -> ElementTree.Element | None:
    if element is None:
        return None
    return next((child for child in element if xml_name(child) == name), None)


def child_text(element: ElementTree.Element | None, name: str) -> str:
    child = first_child(element, name)
    return "" if child is None or child.text is None else child.text.strip()


def package_metadata(nuspec: Path | None) -> tuple[str, str, str, str]:
    if nuspec is None:
        return "not declared in nuspec", "", "", ""

    root = ElementTree.parse(nuspec).getroot()
    metadata = next((element for element in root.iter() if xml_name(element) == "metadata"), None)
    license_node = first_child(metadata, "license")
    if license_node is None:
        license_text = "not declared in nuspec"
    else:
        value = (license_node.text or "").strip()
        license_type = license_node.attrib.get("type")
        license_text = f"{license_type}: {value}" if license_type else value

    return (
        license_text,
        child_text(metadata, "authors"),
        child_text(metadata, "copyright"),
        child_text(metadata, "projectUrl"),
    )


def safe_notice_name(prefix: str, relative_path: Path) -> str:
    combined = f"{prefix}-{relative_path.as_posix()}"
    return INVALID_ARCHIVE_NAME.sub("_", combined)


def copy_notices(source: Path, destination: Path, prefix: str) -> None:
    for candidate in sorted(source.rglob("*"), key=lambda path: path.as_posix().lower()):
        if candidate.is_file() and NOTICE_NAME.search(candidate.name):
            relative = candidate.relative_to(source)
            shutil.copyfile(candidate, destination / safe_notice_name(prefix, relative))


def runtime_pack_version(version_range: str) -> str:
    value = version_range.strip().strip("[]()")
    version = value.split(",", 1)[0].strip()
    if not version:
        raise RuntimeError(f"Could not parse runtime pack version from '{version_range}'.")
    return version


def copy_package_licenses(
    assets_path: Path,
    destination: Path,
    version: str,
    runtime: str,
) -> None:
    assets = json.loads(assets_path.read_text(encoding="utf-8-sig"))
    package_folders = assets.get("packageFolders", {})
    if not package_folders:
        raise RuntimeError("NuGet package folder is missing from project.assets.json.")
    package_folder = Path(next(iter(package_folders)))

    destination.mkdir(parents=True, exist_ok=True)
    inventory = [
        f"bdeyes {version} third-party package inventory",
        "Generated from the resolved NuGet metadata used by this build.",
        "Copied license and notice files are stored beside this inventory.",
        "",
    ]

    libraries = assets.get("libraries", {})
    for package_name, package in sorted(libraries.items(), key=lambda item: item[0].lower()):
        if package.get("type") != "package":
            continue
        package_id, package_version = package_name.rsplit("/", 1)
        package_path = package_folder / package["path"]
        nuspecs = sorted(package_path.glob("*.nuspec"), key=lambda path: path.name.lower())
        license_text, authors, copyright_text, project_url = package_metadata(
            nuspecs[0] if nuspecs else None
        )

        inventory.extend([f"{package_id} {package_version}", f"  License: {license_text}"])
        if authors:
            inventory.append(f"  Authors: {authors}")
        if copyright_text:
            inventory.append(f"  Copyright: {copyright_text}")
        if project_url:
            inventory.append(f"  Project: {project_url}")
        inventory.append("")
        copy_notices(package_path, destination, f"{package_id}-{package_version}")

    runtime_id = f"Microsoft.NETCore.App.Runtime.{runtime}"
    runtime_dependency: dict[str, str] | None = None
    frameworks = assets.get("project", {}).get("frameworks", {})
    for framework in frameworks.values():
        dependencies = framework.get("downloadDependencies", [])
        if isinstance(dependencies, dict):
            dependencies = dependencies.values()
        runtime_dependency = next(
            (dependency for dependency in dependencies if dependency.get("name") == runtime_id),
            runtime_dependency,
        )

    if runtime_dependency is None:
        raise RuntimeError(f"Resolved .NET runtime pack '{runtime_id}' is missing.")
    runtime_version = runtime_pack_version(runtime_dependency["version"])
    runtime_path = package_folder / runtime_id.lower() / runtime_version
    if not runtime_path.is_dir():
        raise RuntimeError(
            f"Resolved .NET runtime pack '{runtime_id} {runtime_version}' is not installed."
        )

    inventory.extend(
        [
            f"{runtime_id} {runtime_version}",
            "  License: see copied runtime LICENSE and THIRD-PARTY-NOTICES files",
            "  Project: https://github.com/dotnet/runtime",
            "",
        ]
    )
    copy_notices(runtime_path, destination, f"{runtime_id}-{runtime_version}")
    (destination / "PACKAGE-LICENSES.txt").write_text(
        "\n".join(inventory) + "\n",
        encoding="utf-8",
        newline="\n",
    )


def verify_executable(executable: Path, runtime: str, version: str) -> None:
    if not executable.is_file():
        raise RuntimeError(f"Published executable is missing: {executable.name}")

    if runtime == "win-x64" and os.name == "nt":
        environment = os.environ.copy()
        environment["BDEYES_PACKAGE_EXE"] = str(executable)
        result = subprocess.run(
            [
                "powershell.exe",
                "-NoLogo",
                "-NoProfile",
                "-NonInteractive",
                "-Command",
                "(Get-Item -LiteralPath $env:BDEYES_PACKAGE_EXE).VersionInfo.ProductVersion",
            ],
            cwd=REPOSITORY_ROOT,
            env=environment,
            check=True,
            text=True,
            capture_output=True,
        )
        product_version = result.stdout.strip()
        if not product_version.startswith(version):
            raise RuntimeError(
                f"Published product version '{product_version}' does not match '{version}'."
            )

    if runtime == "linux-x64" and os.name == "posix" and not os.access(executable, os.X_OK):
        raise RuntimeError("Published Linux executable does not have an executable mode.")


def deterministic_zip(source: Path, destination: Path) -> None:
    with zipfile.ZipFile(
        destination,
        mode="x",
        compression=zipfile.ZIP_DEFLATED,
        compresslevel=9,
    ) as archive:
        for source_path in sorted(source.rglob("*"), key=lambda path: path.as_posix()):
            if not source_path.is_file():
                continue
            relative = source_path.relative_to(source).as_posix()
            info = zipfile.ZipInfo(relative, date_time=(1980, 1, 1, 0, 0, 0))
            info.compress_type = zipfile.ZIP_DEFLATED
            info.create_system = 3
            info.external_attr = (stat.S_IMODE(source_path.stat().st_mode) & 0xFFFF) << 16
            with source_path.open("rb") as source_stream, archive.open(info, "w") as output_stream:
                shutil.copyfileobj(source_stream, output_stream, length=1024 * 1024)


def deterministic_tar_gz(source: Path, destination: Path) -> None:
    with destination.open("xb") as raw_stream:
        with gzip.GzipFile(
            filename="",
            mode="wb",
            compresslevel=9,
            fileobj=raw_stream,
            mtime=0,
        ) as compressed_stream:
            with tarfile.open(
                fileobj=compressed_stream,
                mode="w",
                format=tarfile.GNU_FORMAT,
            ) as archive:
                for source_path in sorted(source.rglob("*"), key=lambda path: path.as_posix()):
                    relative = source_path.relative_to(source).as_posix()
                    info = archive.gettarinfo(str(source_path), arcname=relative)
                    info.uid = 0
                    info.gid = 0
                    info.uname = ""
                    info.gname = ""
                    info.mtime = 0
                    info.pax_headers = {}
                    if info.isfile():
                        with source_path.open("rb") as source_stream:
                            archive.addfile(info, source_stream)
                    else:
                        archive.addfile(info)


def sha256(path: Path) -> str:
    digest = hashlib.sha256()
    with path.open("rb") as stream:
        for chunk in iter(lambda: stream.read(1024 * 1024), b""):
            digest.update(chunk)
    return digest.hexdigest()


def parse_arguments() -> argparse.Namespace:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--version", help="Release version; must match the project version")
    parser.add_argument("--runtime", required=True, choices=sorted(RUNTIMES))
    parser.add_argument("--output-directory", default="artifacts")
    return parser.parse_args()


def main() -> None:
    arguments = parse_arguments()
    actual_version = project_version()
    requested_version = arguments.version or os.environ.get("RELEASE_TAG") or actual_version
    version = requested_version[1:] if requested_version.startswith("v") else requested_version
    if version != actual_version:
        raise RuntimeError(
            f"Requested version '{requested_version}' does not match project version '{actual_version}'."
        )

    executable_name, archive_suffix = RUNTIMES[arguments.runtime]
    output_directory = Path(arguments.output_directory)
    if not output_directory.is_absolute():
        output_directory = REPOSITORY_ROOT / output_directory
    output_directory = output_directory.resolve()
    output_directory.mkdir(parents=True, exist_ok=True)

    archive_base_name = f"bdeyes-{version}-{arguments.runtime}"
    archive_path = output_directory / f"{archive_base_name}{archive_suffix}"
    checksum_path = Path(f"{archive_path}.sha256")
    archive_path.unlink(missing_ok=True)
    checksum_path.unlink(missing_ok=True)

    with tempfile.TemporaryDirectory(prefix="bdeyes-package-") as staging_root:
        publish_directory = Path(staging_root) / archive_base_name
        publish_directory.mkdir()
        run(
            [
                "dotnet",
                "publish",
                str(PROJECT),
                "--configuration",
                "Release",
                "--runtime",
                arguments.runtime,
                "--self-contained",
                "true",
                "-p:PublishSingleFile=true",
                "-p:PublishTrimmed=false",
                "-p:DebugType=None",
                "-p:DebugSymbols=false",
                "--output",
                str(publish_directory),
            ]
        )

        for symbol in publish_directory.rglob("*.pdb"):
            symbol.unlink()
        if any(publish_directory.rglob("*.pdb")):
            raise RuntimeError("Debug symbols remain in the publish directory.")

        verify_executable(
            publish_directory / executable_name,
            arguments.runtime,
            version,
        )
        for repository_file in ("LICENSE", "README.md", "SECURITY.md"):
            shutil.copyfile(REPOSITORY_ROOT / repository_file, publish_directory / repository_file)

        assets_path = REPOSITORY_ROOT / "src" / "Bdeyes" / "obj" / "project.assets.json"
        copy_package_licenses(
            assets_path,
            publish_directory / "licenses",
            version,
            arguments.runtime,
        )

        if arguments.runtime == "win-x64":
            deterministic_zip(publish_directory, archive_path)
        else:
            deterministic_tar_gz(publish_directory, archive_path)

    archive_hash = sha256(archive_path)
    checksum_path.write_text(
        f"{archive_hash} *{archive_path.name}\n",
        encoding="ascii",
        newline="\n",
    )
    print(
        json.dumps(
            {
                "version": version,
                "runtime": arguments.runtime,
                "archive": str(archive_path),
                "checksum": str(checksum_path),
                "sha256": archive_hash,
                "bytes": archive_path.stat().st_size,
            },
            indent=2,
        )
    )


if __name__ == "__main__":
    main()
