#!/usr/bin/env python3
"""Locally project an app-authored first-party aggregate to a small safe receipt.

No network, writes or tunnel provisioning. Prints only a fixed JSON result, and
never prints the source path, raw data or any validation error details.
"""
import pathlib
import sys
from contract import Refused
from native_summary import canonical_projection

def main():
    if len(sys.argv) != 2:
        print("REFUSED_INPUT_COUNT",file=sys.stderr)
        return 2
    try:
        data=pathlib.Path(sys.argv[1]).read_bytes()
        output=canonical_projection(data)
    except (Refused, OSError, UnicodeError, TypeError, ValueError):
        print("REFUSED_NATIVE_SUMMARY_CONTRACT",file=sys.stderr)
        return 1
    sys.stdout.write(output)
    return 0

if __name__=="__main__":
    raise SystemExit(main())
