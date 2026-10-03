"""Swappable engines. Each module maps config `engine:` names to classes.

Engine constructors must be cheap; load models in warm_up(), which the
server calls once in the background at startup.
"""
