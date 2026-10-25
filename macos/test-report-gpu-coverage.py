import importlib.util
import pathlib
import unittest

spec = importlib.util.spec_from_file_location(
    "gpu_coverage", pathlib.Path(__file__).with_name("report-gpu-coverage.py"))
module = importlib.util.module_from_spec(spec)
spec.loader.exec_module(module)


class CoverageReportTests(unittest.TestCase):
    def test_preserves_measured_counts_and_excludes_unrelated_files(self):
        summary = {"lines": {"count": 10, "covered": 7, "percent": 70},
                   "functions": {"count": 4, "covered": 3, "percent": 75}}
        result = module.report({"data": [{"files": [
            {"filename": "/src/GroundMetalProcessing.swift", "summary": summary},
            {"filename": "/src/Other.swift", "summary": summary}]}]})
        self.assertEqual(len(result["files"]), 1)
        self.assertEqual(result["files"][0]["summary"], summary)
        self.assertIn("not instrumented", result["scope"])

    def test_missing_gpu_file_is_not_reported_as_success(self):
        with self.assertRaises(ValueError):
            module.report({"data": [{"files": []}]})


if __name__ == "__main__":
    unittest.main()
