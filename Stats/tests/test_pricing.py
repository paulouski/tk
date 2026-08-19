from __future__ import annotations

import sys
import unittest
from pathlib import Path

sys.path.insert(0, str(Path(__file__).parent.parent))

from tkstats import pricing  # noqa: E402


class KnownCurrentModels(unittest.TestCase):
    """Models seen in real usage must resolve to a non-zero price."""

    def test_current_model_ids_are_priced(self) -> None:
        usage = {"input_tokens": 1_000_000, "output_tokens": 0}
        for model in (
            "claude-opus-5",
            "claude-sonnet-5",
            "gpt-5.4",
            "gpt-5.5",
            "gpt-5.6-luna",
            "gpt-5.6-sol",
            "gpt-5.6-terra",
            "glm-5.3",
        ):
            with self.subTest(model=model):
                self.assertTrue(pricing.is_known_model(model))
                self.assertGreater(pricing.cost_for_usage(usage, model), 0.0)


if __name__ == "__main__":
    unittest.main()
