"""
pricing.py — API cost calculation for Claude models.

Prices in USD per 1M tokens.
"""

from __future__ import annotations

# ---------------------------------------------------------------------------
# Price table  (USD per 1M tokens)
# ---------------------------------------------------------------------------
# Each entry: (input, output, cache_read, cache_write_5m, cache_write_1h)
_PRICES: dict[str, tuple[float, float, float, float, float]] = {
    # Current models
    "claude-opus-4-8":   (5.00, 25.00, 0.50, 6.25, 10.00),
    "claude-sonnet-4-6": (3.00, 15.00, 0.30, 3.75,  6.00),
    "claude-haiku-4-5":  (1.00,  5.00, 0.10, 1.25,  2.00),
    # Older models — same tier as current counterpart
    "claude-opus-4-7":   (5.00, 25.00, 0.50, 6.25, 10.00),
    "claude-sonnet-4-5": (3.00, 15.00, 0.30, 3.75,  6.00),
    # Fable
    "claude-fable-5":    (10.00, 50.00, 1.00, 12.50, 20.00),
}

_unknown_models: set[str] = set()

import re as _re

_DATE_SUFFIX = _re.compile(r"-\d{8}$")


def _normalize_model(model: str) -> str:
    """Resolve a model id to a price-table key.

    If the exact id is unknown, strip a trailing dated suffix (-YYYYMMDD) and
    try the base id (e.g. claude-haiku-4-5-20251001 -> claude-haiku-4-5).
    """
    if model in _PRICES:
        return model
    base = _DATE_SUFFIX.sub("", model)
    if base in _PRICES:
        return base
    return model


def get_unknown_models() -> list[str]:
    """Return list of model names seen but not in the price table."""
    return sorted(_unknown_models)


def is_known_model(model: str) -> bool:
    """True if the model id (or its date-stripped base) is in the price table.

    Empty/missing model ids are NOT known: they price at $0, which would
    silently understate cost. Callers use this to avoid trusting cost-derived
    metrics (e.g. delegation delta) for sessions on un-priced models.
    """
    return bool(model) and _normalize_model(model) in _PRICES


def cost_for_usage(usage: dict, model: str) -> float:
    """
    Calculate USD cost for a single usage dict.

    usage keys used:
      input_tokens, output_tokens, cache_read_input_tokens,
      cache_creation_input_tokens,
      cache_creation.ephemeral_5m_input_tokens (optional),
      cache_creation.ephemeral_1h_input_tokens (optional)

    Returns 0.0 for unknown/missing model (and records it for reporting).
    """
    if not model:
        return 0.0

    prices = _PRICES.get(_normalize_model(model))
    if prices is None:
        _unknown_models.add(model)
        return 0.0

    p_in, p_out, p_cr, p_cw5m, p_cw1h = prices
    M = 1_000_000.0

    input_tokens  = usage.get("input_tokens", 0) or 0
    output_tokens = usage.get("output_tokens", 0) or 0
    cache_read    = usage.get("cache_read_input_tokens", 0) or 0

    # Cache write: use fine-grained breakdown when available
    cache_obj = usage.get("cache_creation") or {}
    cw_5m = cache_obj.get("ephemeral_5m_input_tokens", 0) or 0
    cw_1h = cache_obj.get("ephemeral_1h_input_tokens", 0) or 0
    if cw_5m == 0 and cw_1h == 0:
        # Fall back to flat cache_creation_input_tokens at 5m rate
        cw_5m = usage.get("cache_creation_input_tokens", 0) or 0

    usd = (
        input_tokens  / M * p_in
        + output_tokens / M * p_out
        + cache_read    / M * p_cr
        + cw_5m         / M * p_cw5m
        + cw_1h         / M * p_cw1h
    )
    return usd


def cost_tokens_by_type(n: int, token_type: str, model: str) -> float:
    """
    Cost for N tokens of a given type for a model.
    token_type: "input" | "output" | "cache_read" | "cache_write_5m" | "cache_write_1h"
    Returns 0.0 for unknown model.
    """
    prices = _PRICES.get(_normalize_model(model))
    if prices is None:
        return 0.0
    p_in, p_out, p_cr, p_cw5m, p_cw1h = prices
    M = 1_000_000.0
    rate = {
        "input":         p_in,
        "output":        p_out,
        "cache_read":    p_cr,
        "cache_write_5m": p_cw5m,
        "cache_write_1h": p_cw1h,
    }.get(token_type, 0.0)
    return n / M * rate
