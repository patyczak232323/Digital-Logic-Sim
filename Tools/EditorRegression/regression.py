#!/usr/bin/env python3
"""Static regression probes for editor, persistence, and platform integration.

These checks intentionally report every result instead of aborting on the first
failure.  They complement the solver tests, which cannot exercise Unity GUI
events in the headless CI environment.
"""

from __future__ import annotations

from dataclasses import dataclass
from pathlib import Path
import sys
import time


ROOT = Path(__file__).resolve().parents[2]


def source(path: str) -> str:
    return (ROOT / path).read_text(encoding="utf-8")


@dataclass
class Result:
    name: str
    passed: bool
    details: str


def check_atomic_save_and_recovery() -> Result:
    saver = source("Assets/Scripts/SaveSystem/Saver.cs")
    loader = source("Assets/Scripts/SaveSystem/Loader.cs")
    tokens = (
        'string temporaryPath = path + ".tmp";',
        'string backupPath = path + ".bak";',
        "stream.Flush(true);",
        "File.Move(temporaryPath, path, true);",
    )
    passed = all(token in saver for token in tokens) and "LoadWithBackup" in loader
    return Result("atomic save plus backup recovery", passed, "temporary file, fsync, replacement and .bak fallback")


def check_save_name_collision() -> Result:
    menu = source("Assets/Scripts/Graphics/UI/Menus/ChipSaveMenu.cs")
    passed = (
        "saveButtonEnabled && KeyboardShortcuts.ConfirmShortcutTriggered" in menu
        and "(!nameAlreadyUsed || isNameOfActiveChip)" in menu
    )
    return Result("Enter cannot bypass name validation", passed, "duplicate/self-referential save guard")


def check_case_only_rename() -> Result:
    saver = source("Assets/Scripts/SaveSystem/Saver.cs")
    main_menu = source("Assets/Scripts/Graphics/UI/Menus/MainMenu.cs")
    passed = (
        "MoveDirectorySupportingCaseOnlyRename" in saver
        and "StringComparison.OrdinalIgnoreCase" in saver
        and "isCurrentProjectCaseOnlyRename" in main_menu
    )
    return Result("case-only project and chip rename", passed, "temporary rollback-capable path move")


def check_delete_priority() -> Result:
    controller = source("Assets/Scripts/Game/Interaction/ChipInteractionController.cs")
    selected = controller.find("if (SelectedElements.Count > 0)")
    hovered = controller.find("else if (InteractionState.ElementUnderMouse is WireInstance wire", selected)
    passed = selected >= 0 and hovered > selected
    return Result("Delete affects selection or hovered wire, not both", passed, "exclusive delete branches")


def check_bottom_bar_scroll_routing() -> Result:
    camera = source("Assets/Scripts/Game/Interaction/CameraController.cs")
    bar = source("Assets/Scripts/Graphics/UI/Menus/BottomBarUI.cs")
    passed = "!BottomBarUI.MouseIsOverBar()" in camera and "public static bool MouseIsOverBar()" in bar
    return Result("bottom-bar scrolling does not zoom canvas", passed, "pointer routing guard")


def check_wire_geometry_undo() -> Result:
    controller = source("Assets/Scripts/Game/Interaction/ChipInteractionController.cs")
    undo = source("Assets/Scripts/Game/Interaction/UndoController.cs")
    has_command = any(token in undo for token in ("WireGeometryAction", "WirePointAction", "RecordWireGeometry"))
    records_insert = "RecordWire" in controller[controller.find("InsertPoint") - 300:controller.find("InsertPoint") + 300]
    records_delete = "RecordWire" in controller[controller.find("DeleteWirePoint") - 300:controller.find("DeleteWirePoint") + 300]
    passed = has_command and records_insert and records_delete
    return Result("wire-point edits participate in Undo/Redo", passed, "insert, move and delete require first-class history commands")


def check_native_quit_guard() -> Result:
    unity_main = source("Assets/Scripts/Game/Main/UnityMain.cs")
    passed = (
        "Application.wantsToQuit" in unity_main
        and "ActiveChipHasUnsavedChanges" in unity_main
    )
    return Result("native window close protects unsaved work", passed, "Alt+F4/window-close interception")


def check_layout_aware_key_chip() -> Result:
    keyboard = source("Assets/Scripts/Simulation/SimKeyboardHelper.cs")
    physical_scan = "foreach (KeyCode key in ValidInputKeys)" in keyboard and "char.ToUpper((char)key)" in keyboard
    text_input = "InputString" in keyboard or "Input.inputString" in keyboard
    passed = text_input and not physical_scan
    return Result("KEY chip respects keyboard layout", passed, "character input instead of physical Unity KeyCode mapping")


def check_fixed_aspect_is_explicit() -> Result:
    drawer = source("Assets/Scripts/Graphics/UI/UIDrawer.cs")
    ui = source("Assets/Scripts/Seb/SebVis/UI/UI.cs")
    passed = "CreateFixedAspectUIScope(drawLetterbox: true)" in drawer and "aspectX = 16, int aspectY = 9" in ui
    return Result("16:9 letterboxing is explicit", passed, "intentional compatibility behavior, not an accidental stretch")


CHECKS = (
    check_atomic_save_and_recovery,
    check_save_name_collision,
    check_case_only_rename,
    check_delete_priority,
    check_bottom_bar_scroll_routing,
    check_wire_geometry_undo,
    check_native_quit_guard,
    check_layout_aware_key_chip,
    check_fixed_aspect_is_explicit,
)


def main() -> int:
    started = time.perf_counter()
    failures = 0
    for check in CHECKS:
        result = check()
        status = "PASS" if result.passed else "FAIL"
        failures += not result.passed
        print(f"{status:<5} {result.name:<52} {result.details}")
    print(f"SUMMARY {len(CHECKS) - failures}/{len(CHECKS)} checks passed in {time.perf_counter() - started:.3f}s")
    return 1 if failures else 0


if __name__ == "__main__":
    sys.exit(main())
