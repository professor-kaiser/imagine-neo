"""Launch real WinUI with deterministic imports and validate its persisted IPC report."""

import argparse
import json
import os
from pathlib import Path
import subprocess
import sys
import time
import pywintypes
import win32pipe
from uuid import uuid4
from create_fixtures import main as create_fixtures

sys.path.insert(0, str(Path(__file__).parents[1]))
from app.models.operations import ALL_OPERATIONS


def main() -> None:
    """Run the UI smoke test with a private backend; raise on timeout, exit failure or wrong report.

    CLI arguments: configuration, launch mode, optional fixtures and --expect-error OP=CODE.
    Produces two PNG fixtures and
    a JSON report. It verifies imports and full/ROI grayscale through the UI apply-control
    path, including returned pixels and alpha, but not native picker/Explorer mouse gestures.
    """
    parser = argparse.ArgumentParser(description="Vérification réelle WinUI et IPC, sans implémenter les algorithmes.")
    parser.add_argument("configuration", nargs="?", choices=("Debug", "Release"), default="Debug")
    parser.add_argument("mode", nargs="?", choices=("direct", "run_all", "run_ui"), default="direct")
    parser.add_argument("suite", nargs="?", choices=("production", "fixtures"), default="production")
    parser.add_argument("--expect-error", action="append", default=[], metavar="OPERATION=CODE",
                        help="Vérifier explicitement une erreur d’algorithme connue ; ne la masque pas dans le rapport.")
    parser.add_argument("--default-pipe", action="store_true",
                        help="Tester le démarrage habituel sans imposer un pipe de test isolé.")
    args = parser.parse_args()
    expected_errors = {}
    for item in args.expect_error:
        operation, separator, code = item.partition("=")
        if not separator or operation not in ALL_OPERATIONS or operation == "to_grayscale" or not code.strip() or code == "ok":
            parser.error("Erreur attendue invalide : utilisez une opération préparée et un code d’erreur non vide.")
        expected_errors[operation] = code
    if expected_errors and args.suite == "fixtures":
        parser.error("Le mode fixtures exige des retours positifs, sans erreurs attendues.")
    if args.default_pipe and args.suite == "fixtures":
        parser.error("Les fonctions de test doivent utiliser un pipe isolé.")
    create_fixtures()
    root = Path(__file__).parents[2]
    configuration = args.configuration
    executable = root / f"ui/bin/x64/{configuration}/net8.0-windows10.0.22621.0/win-x64/ImagineNeo.exe"
    if not executable.is_file():
        raise SystemExit("ERREUR : executable absent. Lancez scripts\\build_ui.cmd.")
    report = root / "artifacts" / ("ui-smoke-" + uuid4().hex + ".json")
    environment = os.environ.copy()
    environment["PHOTOLAB_PIPE_NAME"] = "PhotoLab_ui_test_" + uuid4().hex
    if args.default_pipe:
        environment.pop("PHOTOLAB_PIPE_NAME", None)
    environment["PHOTOLAB_PYTHON"] = sys.executable
    environment["PHOTOLAB_ROOT"] = str(root)
    environment["PHOTOLAB_TEST_EXPECTED_ERRORS"] = json.dumps(expected_errors)
    environment.pop("PHOTOLAB_TEST_FIXTURES", None)
    environment.pop("PHOTOLAB_SMOKE_REPORT", None)
    mode = args.mode
    if mode in {"run_all", "run_ui"}:
        environment["PHOTOLAB_SMOKE_REPORT"] = str(report)
        command = ["cmd.exe", "/d", "/c", str(root / "scripts" / f"{mode}.cmd"), configuration]
        working_directory = root / "artifacts"
    elif mode == "direct":
        command = [sys.executable, "-m", "app.launcher", str(executable), "--smoke-test", str(report)]
        working_directory = root / "backend"
    else:
        raise ValueError("Expected direct, run_all or run_ui launch mode")
    fixture_mode = args.suite == "fixtures"
    fixture = None
    if fixture_mode:
        environment["PHOTOLAB_TEST_FIXTURES"] = "1"
        fixture = subprocess.Popen([sys.executable, str(root / "backend/tests/fixture_backend.py"),
                                    "--pipe-name", environment["PHOTOLAB_PIPE_NAME"]],
                                   cwd=root / "backend", env=environment, creationflags=subprocess.CREATE_NO_WINDOW)
    try:
        if fixture is not None:
            deadline = time.monotonic() + 15
            while True:
                if fixture.poll() is not None or time.monotonic() > deadline:
                    raise RuntimeError("Test fixture backend did not become ready")
                try:
                    win32pipe.WaitNamedPipe("\\\\.\\pipe\\" + environment["PHOTOLAB_PIPE_NAME"], 100)
                    break
                except pywintypes.error:
                    time.sleep(0.05)
        process = subprocess.Popen(command, cwd=working_directory, env=environment)
        try:
            code = process.wait(timeout=180 if mode == "run_all" else 60)
            if code != 0:
                raise subprocess.CalledProcessError(code, process.args)
        except subprocess.TimeoutExpired:
            subprocess.run(["taskkill", "/PID", str(process.pid), "/T", "/F"], check=False, capture_output=True)
            process.wait(timeout=10)
            raise
    finally:
        if fixture is not None:
            if fixture.poll() is None:
                fixture.terminate()
            fixture.wait(timeout=10)
    result = json.loads(report.read_text(encoding="utf-8"))
    if (result.get("status") != "ok" or result.get("count", 0) < 4
            or result.get("grayscale_results") != 2 or not result.get("grayscale_verified")
            or not result.get("all_operations_wired") or not result.get("backend_revision_verified")
            or not result.get("drawing_masks_verified") or not result.get("editing_history_verified")
            or not result.get("image_export_verified") or not result.get("drawing_apply_verified")
            or not result.get("plugin_forms_verified") or not result.get("plugin_presentations_verified")
            or not result.get("plugin_full_roi_history_verified")
            or not result.get("masks_creation_parameters_history_verified") or not result.get("masks_plugin_selection_verified")
            or not result.get("mask_hsv_palette_verified") or not result.get("paint_mask_history_verified")
            or not result.get("public_color_palettes_verified")):
        raise AssertionError(result)
    if result.get("transform_controls_verified") != 3 or any(value not in ({"ok"} if fixture_mode else {"ok", "not_implemented"}) for value in result.get("transform_operation_outcomes", {}).values()):
        raise AssertionError(result)
    if not fixture_mode and not result.get("drawing_compositing_verified"):
        raise AssertionError(result)
    if fixture_mode and (result.get("count") != 11 or any(value != "ok" for value in result["operation_outcomes"].values())):
        raise AssertionError(result)
    for operation, code in expected_errors.items():
        if result["operation_outcomes"].get(operation) != code or not result.get("expected_errors_verified"):
            raise AssertionError(result)
    report_name = "ui-fixtures-report.json" if fixture_mode else "ui-error-report.json" if expected_errors else "ui-smoke-report.json"
    (root / "artifacts" / report_name).write_text(json.dumps(result, indent=2), encoding="utf-8")
    print(f"PASS ({mode}, fixtures={fixture_mode}, erreurs attendues={expected_errors}) : WinUI, dix outils raccordes. Rapport : {report}")


if __name__ == "__main__":
    main()
