#!/usr/bin/env python3
"""Feed pinned public module metadata to the official CLI schema extractor hook.

SPACETIMEDB_SCHEMA_EXTRACTOR is supported by CLI 2.10.0 generate.rs; this only
supplies schema metadata. The official generator owns all C# protocol/binding code.
"""
import json
from pathlib import Path

schema = json.loads((Path(__file__).resolve().parents[1] / 'Bindings/schema.json').read_text())
print(json.dumps(schema, separators=(',', ':')))
