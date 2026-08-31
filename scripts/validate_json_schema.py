#!/usr/bin/env python3
"""Validate Excise registries with the JSON Schema features they use."""

from __future__ import annotations

import argparse
import json
from pathlib import Path
import re
from typing import Any


def _resolve_pointer(root: dict[str, Any], reference: str) -> dict[str, Any]:
    if not reference.startswith("#/"):
        raise ValueError(f"only local schema references are supported: {reference}")
    value: Any = root
    for raw in reference[2:].split("/"):
        token = raw.replace("~1", "/").replace("~0", "~")
        value = value[token]
    if not isinstance(value, dict):
        raise ValueError(f"schema reference is not an object: {reference}")
    return value


def _matches_type(value: Any, expected: str) -> bool:
    return {
        "null": value is None,
        "object": isinstance(value, dict),
        "array": isinstance(value, list),
        "string": isinstance(value, str),
        "boolean": isinstance(value, bool),
        "integer": isinstance(value, int) and not isinstance(value, bool),
        "number": isinstance(value, (int, float)) and not isinstance(value, bool),
    }.get(expected, False)


def validate_json_schema(
    value: Any,
    schema: dict[str, Any],
    *,
    root_schema: dict[str, Any] | None = None,
    path: str = "$",
) -> list[str]:
    root = schema if root_schema is None else root_schema
    if "$ref" in schema:
        try:
            referenced = _resolve_pointer(root, schema["$ref"])
        except (KeyError, TypeError, ValueError) as exc:
            return [f"{path}: invalid schema reference: {exc}"]
        return validate_json_schema(value, referenced, root_schema=root, path=path)

    errors: list[str] = []
    expected_types = schema.get("type")
    if isinstance(expected_types, str):
        expected_types = [expected_types]
    if expected_types is not None and not any(
        _matches_type(value, expected) for expected in expected_types
    ):
        return [f"{path}: expected type {' or '.join(expected_types)}"]

    if "const" in schema and value != schema["const"]:
        errors.append(f"{path}: expected constant {schema['const']!r}")
    if "enum" in schema and value not in schema["enum"]:
        errors.append(f"{path}: value {value!r} is not in the allowed enum")

    if isinstance(value, dict):
        required = schema.get("required", [])
        for key in required:
            if key not in value:
                errors.append(f"{path}: missing required property {key!r}")
        properties = schema.get("properties", {})
        for key, item in value.items():
            item_path = f"{path}.{key}"
            if key in properties:
                errors.extend(
                    validate_json_schema(
                        item,
                        properties[key],
                        root_schema=root,
                        path=item_path,
                    )
                )
            elif schema.get("additionalProperties") is False:
                errors.append(f"{item_path}: additional property is not allowed")

    if isinstance(value, list):
        if len(value) < schema.get("minItems", 0):
            errors.append(f"{path}: has fewer than {schema['minItems']} items")
        if "maxItems" in schema and len(value) > schema["maxItems"]:
            errors.append(f"{path}: has more than {schema['maxItems']} items")
        if schema.get("uniqueItems"):
            encoded = [
                json.dumps(item, sort_keys=True, separators=(",", ":"))
                for item in value
            ]
            if len(encoded) != len(set(encoded)):
                errors.append(f"{path}: items must be unique")
        item_schema = schema.get("items")
        if isinstance(item_schema, dict):
            for index, item in enumerate(value):
                errors.extend(
                    validate_json_schema(
                        item,
                        item_schema,
                        root_schema=root,
                        path=f"{path}[{index}]",
                    )
                )

    if isinstance(value, str):
        if len(value) < schema.get("minLength", 0):
            errors.append(f"{path}: string is shorter than {schema['minLength']}")
        pattern = schema.get("pattern")
        if pattern is not None and re.search(pattern, value) is None:
            errors.append(f"{path}: string does not match {pattern!r}")

    if isinstance(value, (int, float)) and not isinstance(value, bool):
        if "minimum" in schema and value < schema["minimum"]:
            errors.append(f"{path}: value is below {schema['minimum']}")
        if "maximum" in schema and value > schema["maximum"]:
            errors.append(f"{path}: value is above {schema['maximum']}")

    return errors


def load_object(path: Path) -> dict[str, Any]:
    value = json.loads(path.read_text(encoding="utf-8"))
    if not isinstance(value, dict):
        raise ValueError(f"{path} must contain a JSON object")
    return value


def self_test() -> int:
    schema = {
        "type": "object",
        "additionalProperties": False,
        "required": ["schemaVersion", "items"],
        "properties": {
            "schemaVersion": {"const": 1},
            "items": {"$ref": "#/$defs/items"},
        },
        "$defs": {
            "items": {
                "type": "array",
                "minItems": 1,
                "uniqueItems": True,
                "items": {
                    "type": "string",
                    "minLength": 2,
                    "pattern": "^[a-z]+$",
                },
            }
        },
    }
    valid = {"schemaVersion": 1, "items": ["alpha", "beta"]}
    mutations = [
        {"items": valid["items"]},
        {**valid, "schemaVersion": 2},
        {**valid, "items": ["alpha", "alpha"]},
        {**valid, "items": ["A"]},
        {**valid, "unexpected": True},
    ]
    if validate_json_schema(valid, schema) or any(
        not validate_json_schema(mutation, schema) for mutation in mutations
    ):
        print("FAIL: JSON Schema validator self-test", flush=True)
        return 1
    print("PASS: JSON Schema validator rejects structural mutations")
    return 0


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("document", nargs="?", type=Path)
    parser.add_argument("schema", nargs="?", type=Path)
    parser.add_argument("--self-test", action="store_true")
    args = parser.parse_args()
    if args.self_test:
        return self_test()
    if args.document is None or args.schema is None:
        parser.error("document and schema are required unless --self-test is used")
    try:
        errors = validate_json_schema(
            load_object(args.document),
            load_object(args.schema),
        )
    except (OSError, json.JSONDecodeError, ValueError) as exc:
        print(f"FAIL: {exc}")
        return 1
    if errors:
        print(f"FAIL: {args.document} does not match {args.schema}")
        for error in errors:
            print(f"  - {error}")
        return 1
    print(f"PASS: {args.document} matches {args.schema}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
