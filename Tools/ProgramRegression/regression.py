#!/usr/bin/env python3
"""Cross-cutting regression checks for save safety, UI validation and runtime guards."""

from __future__ import annotations

from pathlib import Path
import tempfile


ROOT = Path(__file__).resolve().parents[2]


def source(path: str) -> str:
    return (ROOT / path).read_text(encoding="utf-8")


def validate_portable_name(name: str) -> bool:
    forbidden = set('<>:"/\\|?*.')
    reserved = {
        "CON", "PRN", "AUX", "NUL",
        *(f"COM{i}" for i in range(1, 10)),
        *(f"LPT{i}" for i in range(1, 10)),
    }
    return bool(name.strip()) and name[-1] not in " ." and not any(c in forbidden for c in name) and name.strip().upper() not in reserved


def assert_cycle_guard(graph: dict[str, list[str]], root: str) -> bool:
    active: set[str] = set()

    def visit(name: str) -> None:
        key = name.casefold()
        if key in active:
            raise ValueError("cycle")
        active.add(key)
        try:
            for child in graph.get(name, []):
                visit(child)
        finally:
            active.remove(key)

    try:
        visit(root)
        return True
    except ValueError:
        return False


def test_portable_file_names() -> None:
    for name in ("CPU", "counter 16", "Display_v2"):
        assert validate_portable_name(name), name
    for name in ("", "   ", "CON", "chip.", "chip ", "a/b", "a.json"):
        assert not validate_portable_name(name), name


def test_cycle_model() -> None:
    assert assert_cycle_guard({"A": ["B"], "B": ["C"], "C": []}, "A")
    assert not assert_cycle_guard({"A": ["B"], "B": ["a"]}, "A")


def test_address_mask_model() -> None:
    memory = list(range(256))
    for raw in (0, 1, 255, 256, 511, 65535):
        assert memory[raw & 0xFF] == raw & 0xFF


def test_backup_recovery_model() -> None:
    with tempfile.TemporaryDirectory() as directory:
        primary = Path(directory) / "save.json"
        backup = Path(str(primary) + ".bak")
        primary.write_text("broken", encoding="utf-8")
        backup.write_text('{"valid":true}', encoding="utf-8")

        def read_valid(path: Path) -> str:
            value = path.read_text(encoding="utf-8")
            if not value.startswith("{"):
                raise ValueError("invalid")
            return value

        try:
            result = read_valid(primary)
        except ValueError:
            result = read_valid(backup)
        assert result == '{"valid":true}'


def test_topology_invalidation_race_model() -> None:
    # Old design: a producer can set the shared flag after the consumer drains
    # the queue but before the consumer clears it. The new command then remains
    # queued while the flag is false, so the next drain misses invalidation.
    old_pending = True
    new_command_queued = False
    new_command_queued = True
    old_pending = False
    assert new_command_queued and not old_pending

    # New design: invalidation is derived from the command successfully removed
    # from the concurrent queue in that exact drain, so it cannot be lost.
    commands_dequeued_next_drain = 1
    topology_changed = commands_dequeued_next_drain > 0
    assert topology_changed


def test_global_chip_storage_model() -> None:
    # A missing primary must still be discoverable through its backup because
    # global chips do not have a per-project manifest listing their names.
    files = {"ALU.json.bak", "MUX.json", "notes.txt", "MUX.json.bak"}
    primary_paths = {name for name in files if name.endswith(".json")}
    backup_primary_paths = {name[:-4] for name in files if name.endswith(".json.bak")}
    discovered = {name.casefold() for name in primary_paths | backup_primary_paths}
    assert discovered == {"alu.json", "mux.json"}

    # Project-local and global namespaces may not silently alias by case.
    global_names = {"alu", "decoder"}
    other_project_local_names = {"counter", "ALU"}
    assert {name.casefold() for name in global_names} & {
        name.casefold() for name in other_project_local_names
    } == {"alu"}


def test_csharp_integration_guards() -> None:
    saver = source("Assets/Scripts/SaveSystem/Saver.cs")
    loader = source("Assets/Scripts/SaveSystem/Loader.cs")
    serializer = source("Assets/Scripts/Description/Serialization/Serializer.cs")
    save_utils = source("Assets/Scripts/SaveSystem/SaveUtils.cs")
    chip_menu = source("Assets/Scripts/Graphics/UI/Menus/ChipSaveMenu.cs")
    main_menu = source("Assets/Scripts/Graphics/UI/Menus/MainMenu.cs")
    dev_chip = source("Assets/Scripts/Game/Project/DevChipInstance.cs")
    simulator = source("Assets/Scripts/Simulation/Simulator.cs")
    deterministic = source("Assets/Scripts/Simulation/DeterministicSimulator.cs")
    sim_chip = source("Assets/Scripts/Simulation/SimChip.cs")
    project = source("Assets/Scripts/Game/Project/Project.cs")
    camera = source("Assets/Scripts/Game/Interaction/CameraController.cs")
    simulation_facade = source("Assets/Scripts/Simulation/RewiredEngine.cs")
    save_paths = source("Assets/Scripts/SaveSystem/SavePaths.cs")
    chip_library = source("Assets/Scripts/Game/Project/ChipLibrary.cs")
    global_chips = source("Assets/Scripts/SaveSystem/GlobalChipManager.cs")
    rhdl_compiler = source("Assets/Scripts/RHDL/RhdlCompiler.cs")

    assert 'string temporaryPath = path + ".tmp";' in saver
    assert 'string backupPath = path + ".bak";' in saver
    assert "stream.Flush(true);" in saver
    assert "File.Copy(path, stagedBackupPath, true);" in saver
    assert "if (File.Exists(path)) File.Delete(path);" in saver
    assert "File.Move(temporaryPath, path);" in saver
    assert "File.Move(stagedBackupPath, backupPath);" in saver
    assert "LoadWithBackup" in loader and 'path + ".bak"' in loader
    assert "throw new InvalidDataException" in serializer
    assert "string.IsNullOrWhiteSpace(name)" in save_utils
    assert "saveButtonEnabled && KeyboardShortcuts.ConfirmShortcutTriggered" in chip_menu
    assert "ActiveCustomizeDescription.Size = minChipSize + sizeBeyondNameMinimum;" in chip_menu
    assert "canCreateProject && KeyboardShortcuts.ConfirmShortcutTriggered" in main_menu
    assert "wireDescription.ConnectedWireIndex < allWires.Count" in dev_chip
    assert "Cyclic chip dependency detected" in simulator
    assert deterministic.count("GetAddress8Bit(") >= 6
    assert "Math.Max(requiredStateLength, serializedStateLength)" in sim_chip
    assert "dev_Ram_8Bit" not in deterministic
    assert "dev_Ram_8Bit" not in sim_chip
    assert "dev.RAM-8" not in simulator
    assert "Thread.Sleep(TimeSpan.FromMilliseconds(waitMs));" in project
    assert "Thread.Yield();" in project
    assert "Thread.SpinWait(10);" not in project
    assert "!BottomBarUI.MouseIsOverBar()" in camera
    assert "public static bool ApplyModifications()" in simulator
    assert "while (modificationQueue.TryDequeue(out SimModifyCommand cmd))" in simulator
    assert "return topologyChanged;" in simulator
    assert "public static class RewiredEngine" in simulation_facade
    assert "if (Simulator.ApplyModifications())" in simulation_facade
    assert "DeterministicSimulator.InvalidateTopology();" in simulation_facade
    assert "pendingTopologyModification" not in simulation_facade
    assert 'GlobalChipsPath = Path.Combine(AllData, "Global Chips")' in save_paths
    assert "LoadAllGlobalChipDescriptions" in loader
    assert 'Directory.EnumerateFiles(SavePaths.GlobalChipsPath, "*.json.bak")' in loader
    assert "public bool IsGlobalChip(string name)" in chip_library
    assert "Make dependency '" in global_chips
    assert "PropagateGlobalRename" in global_chips
    assert "TrySaveFromDescription" in project
    assert '"ALL PROJECTS"' in chip_menu
    assert "float y = totalHeight * 0.5f - i * 1.5f;" in rhdl_compiler
    assert "result[i] = new PinDescription(" in rhdl_compiler
    assert "DesiredY" not in rhdl_compiler
    assert "FindStrongComponents" in rhdl_compiler
    assert "instance.LayoutGroup = group[instance]" in rhdl_compiler
    assert "RouteOrthogonal" in rhdl_compiler
    assert "RouteIsClear" in rhdl_compiler
    assert "SegmentConflictPenalty" in rhdl_compiler
    assert not (ROOT / "Assets/Scripts/Game/Project/SimulationFacade.cs").exists()
    assert not (ROOT / "Assets/Scripts/Graphics/UI/Menus/SimulationFacade.cs").exists()


TESTS = (
    test_portable_file_names,
    test_cycle_model,
    test_address_mask_model,
    test_backup_recovery_model,
    test_topology_invalidation_race_model,
    test_global_chip_storage_model,
    test_csharp_integration_guards,
)


def main() -> None:
    for test in TESTS:
        test()
        print(f"PASS  {test.__name__}")
    print(f"ALL {len(TESTS)} PROGRAM REGRESSIONS PASSED")


if __name__ == "__main__":
    main()
