"""Preflight the complete plugin catalogue before a combined launch."""
from app.plugins.catalog import get_plugin_registry


def main():
    registry = get_plugin_registry()
    if registry.errors:
        for error in registry.errors:
            print(error)
        return 1
    masks = sum(p.output_kind == "mask" for p in registry.definitions.values())
    print(f"Plugins ready: {len(registry.definitions) - masks} transformations, {masks} mask generators.")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
