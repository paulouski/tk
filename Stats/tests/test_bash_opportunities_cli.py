from __future__ import annotations

import subprocess
import sys
import unittest
from pathlib import Path


class BashOpportunitiesCli(unittest.TestCase):
    def test_help_starts_without_import_errors(self) -> None:
        script = Path(__file__).parent.parent / "bash_opportunities.py"
        result = subprocess.run(
            [sys.executable, str(script), "--help"],
            capture_output=True,
            text=True,
            check=False,
        )

        self.assertEqual(result.returncode, 0, result.stderr)
        self.assertIn("--read-cost", result.stdout)


if __name__ == "__main__":
    unittest.main()
