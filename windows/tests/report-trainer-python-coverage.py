"""Verify the installed trainer entry points ran before folding their hits into source paths."""

import argparse
from pathlib import Path

from coverage import Coverage, CoverageData


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("config", type=Path)
    parser.add_argument("installed_training", type=Path)
    parser.add_argument("report", type=Path)
    args = parser.parse_args()

    coverage = Coverage(config_file=str(args.config))
    data_file = Path(coverage.get_option("run:data_file"))
    installed = args.installed_training.resolve()
    required = {
        installed / "BotPython/train_bot_job.py",
        installed / "BotPython/train_hacker_gym.py",
        installed / "BotPython/compare_policy_inputs.py",
        installed / "TrainingPython/hkv_incremental_trainer.py",
    }
    measured = set()
    for counters in data_file.parent.glob(f"{data_file.name}.*"):
        process_data = CoverageData(basename=str(counters))
        process_data.read()
        measured.update(Path(name).resolve() for name in process_data.measured_files() if process_data.lines(name))
    if missing := required - measured:
        raise SystemExit("No execution counters for installed trainer entry points:\n" + "\n".join(map(str, sorted(missing))))

    coverage.combine(strict=True)
    if not coverage.get_data().has_arcs():
        raise SystemExit("Windows trainer coverage is missing branch measurements.")
    coverage.json_report(outfile=str(args.report))
    for path in sorted(required):
        print(f"PASS Windows coverage measured installed {path.relative_to(installed).as_posix()}")


if __name__ == "__main__":
    main()
