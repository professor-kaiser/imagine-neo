"""YAML registry, constructor binding and dynamic class factory."""

from dataclasses import dataclass
from functools import lru_cache
import hashlib
import importlib.util
import inspect
import keyword
import math
from pathlib import Path
import re
import sys
from types import ModuleType
import typing

import yaml

from .base import ImagePlugin
from .parameters import validate_hsv, validate_bgra

PLUGIN_ROOT = Path(__file__).resolve().parents[3] / "plugins"
TYPES = {"text": str, "integer": int, "number": float, "boolean": bool, "hsv": list, "color_hsv": list, "color_bgra": list}


class PluginConfigurationError(ValueError):
    code = "plugin_configuration"


class UniqueSafeLoader(yaml.SafeLoader):
    """Safe YAML with duplicate keys rejected, including parameter names."""


def _mapping(loader, node, deep=False):
    result = {}
    for key_node, value_node in node.value:
        key = loader.construct_object(key_node, deep=deep)
        if not isinstance(key, str) or key in result:
            raise PluginConfigurationError(f"Duplicate or non-text YAML key: {key!r}")
        result[key] = loader.construct_object(value_node, deep=deep)
    return result


UniqueSafeLoader.add_constructor(yaml.resolver.BaseResolver.DEFAULT_MAPPING_TAG, _mapping)


def _text(value, name, maximum=256):
    if not isinstance(value, str) or not value.strip() or len(value) > maximum:
        raise PluginConfigurationError(f"{name}: expected nonempty text (max {maximum})")
    return value


@dataclass(frozen=True)
class Parameter:
    name: str
    kind: str
    label: str
    description: str
    has_default: bool
    default: object = None
    minimum: float | int | None = None
    maximum: float | int | None = None
    choices: tuple[str, ...] = ()
    visible_when: dict[str, list[str]] | None = None
    palette_channel: str | None = None

    @classmethod
    def parse(cls, name, data):
        if not isinstance(name, str) or not name.isidentifier() or keyword.iskeyword(name) or name in {"self", "image", "selection", "masks"}:
            raise PluginConfigurationError(f"Invalid/reserved constructor parameter: {name}")
        if not isinstance(data, dict) or set(data) - {"type", "label", "description", "default", "min", "max", "choices", "visible_when", "palette_channel"}:
            raise PluginConfigurationError(f"{name}: unknown parameter fields")
        kind = data.get("type")
        if kind not in TYPES:
            raise PluginConfigurationError(f"{name}: type must be text, integer, number, boolean, hsv, color_hsv or color_bgra")
        low, high = data.get("min"), data.get("max")
        for bound in (low, high):
            if bound is not None and (kind not in {"integer", "number"} or type(bound) not in (int, float)
                                      or not math.isfinite(bound) or abs(bound) > 9007199254740991
                                      or kind == "integer" and int(bound) != bound):
                raise PluginConfigurationError(f"{name}: invalid numeric bounds")
        if low is not None and high is not None and low > high:
            raise PluginConfigurationError(f"{name}: min must not exceed max")
        description = data.get("description", "")
        if not isinstance(description, str) or len(description) > 2048:
            raise PluginConfigurationError(f"{name}: invalid description")
        choices = data.get("choices", [])
        if ("choices" in data and (kind != "text" or not isinstance(choices, list)
                or not 1 <= len(choices) <= 64
                or any(not isinstance(v, str) or not v.strip() or len(v) > 256 for v in choices)
                or len(set(choices)) != len(choices))):
            raise PluginConfigurationError(f"{name}: choices must be unique nonempty text values")
        visible_when = data.get("visible_when")
        if "visible_when" in data and (not isinstance(visible_when, dict) or not visible_when
                or "default" not in data
                or any(not isinstance(key, str) or not isinstance(values, list) or not values
                       or any(not isinstance(v, str) for v in values) for key, values in visible_when.items())):
            raise PluginConfigurationError(f"{name}: visible_when needs choice lists and a default")
        palette = data.get("palette_channel")
        if "palette_channel" in data and (kind != "integer" or not isinstance(palette, str)
                or low != 0 or high != 255):
            raise PluginConfigurationError(f"{name}: palette_channel requires an integer in [0, 255]")
        result = cls(name, kind, _text(data.get("label", name), name), description,
                     "default" in data, data.get("default"), low, high, tuple(choices), visible_when, palette)
        if result.has_default:
            result.validate(result.default)
        return result

    def validate(self, value):
        if self.kind in {"hsv", "color_hsv"}:
            return validate_hsv(value, self.label)
        if self.kind == "color_bgra":
            return validate_bgra(value, self.label)
        valid = (type(value) is str if self.kind == "text" else
                 type(value) is bool if self.kind == "boolean" else
                 type(value) is int if self.kind == "integer" else type(value) in (int, float))
        if not valid:
            raise ValueError(f"{self.label}: expected {self.kind}")
        if self.kind in {"integer", "number"}:
            if not math.isfinite(value) or abs(value) > 9007199254740991:
                raise ValueError(f"{self.label}: expected a finite, representable number")
            if self.minimum is not None and value < self.minimum or self.maximum is not None and value > self.maximum:
                raise ValueError(f"{self.label}: value outside [{self.minimum}, {self.maximum}]")
            return float(value) if self.kind == "number" else value
        if self.choices and value not in self.choices:
            raise ValueError(f"{self.label}: choose one of {', '.join(self.choices)}")
        if self.kind == "text" and len(value) > 4096:
            raise ValueError(f"{self.label}: text exceeds 4096 characters")
        return value

    def describe(self):
        result = {"name": self.name, "type": self.kind, "label": self.label,
                  "description": self.description, "required": not self.has_default}
        if self.has_default:
            result["default"] = self.default
        if self.minimum is not None:
            result["min"] = self.minimum
        if self.maximum is not None:
            result["max"] = self.maximum
        if self.choices:
            result["choices"] = list(self.choices)
        if self.visible_when:
            result["visible_when"] = self.visible_when
        if self.palette_channel:
            result["palette_channel"] = self.palette_channel
        return result


@dataclass(frozen=True)
class PluginDefinition:
    id: str
    name: str
    description: str
    plugin_class: type[ImagePlugin]
    parameters: tuple[Parameter, ...]
    output_kind: str = "image"
    input_kind: str = "image"

    def bind(self, supplied):
        if not isinstance(supplied, dict) or supplied.keys() - {p.name for p in self.parameters}:
            raise ValueError(f"{self.name}: unknown constructor parameters")
        values = {}
        for parameter in self.parameters:
            if parameter.name in supplied:
                value = supplied[parameter.name]
            elif parameter.has_default:
                value = parameter.default
            else:
                raise ValueError(f"{parameter.label}: required parameter")
            values[parameter.name] = parameter.validate(value)
        return values

    def describe(self):
        return {"id": self.id, "name": self.name, "description": self.description,
                "parameters": [p.describe() for p in self.parameters], "output_kind": self.output_kind, "input_kind": self.input_kind}


class PluginFactory:
    """Factory: resolve a configured class once, create a fresh strategy for each run."""

    @staticmethod
    def load(root: Path, relative: str, class_name: str) -> type[ImagePlugin]:
        candidate = Path(relative)
        path = (root / candidate).resolve()
        if candidate.is_absolute() or not path.is_relative_to(root) or len(candidate.parts) < 2 or path.suffix != ".py":
            raise PluginConfigurationError("file must be a .py file inside a plugin subdirectory")
        if not path.is_file():
            raise PluginConfigurationError(f"Plugin file does not exist: {relative}")
        # Namespace packages permit imports such as 'from .helpers import ...'
        # without changing sys.path or colliding with another plugin's helpers.
        namespace = "_imagine_plugin_" + hashlib.sha256(str(root).encode()).hexdigest()[:16]
        package = namespace
        if package not in sys.modules:
            module = ModuleType(package)
            module.__path__ = [str(root)]
            sys.modules[package] = module
        folder = root
        for part in path.parent.relative_to(root).parts:
            if not part.isidentifier() or keyword.iskeyword(part):
                raise PluginConfigurationError("Plugin folder names must be Python identifiers")
            folder /= part
            package += "." + part
            if package not in sys.modules:
                module = ModuleType(package)
                module.__path__ = [str(folder)]
                sys.modules[package] = module
        if not path.stem.isidentifier():
            raise PluginConfigurationError("Plugin filenames must be Python identifiers")
        module_name = package + "." + path.stem
        if module_name not in sys.modules:
            spec = importlib.util.spec_from_file_location(module_name, path)
            module = importlib.util.module_from_spec(spec)
            sys.modules[module_name] = module
            try:
                spec.loader.exec_module(module)
            except BaseException:
                sys.modules.pop(module_name, None)
                raise
        plugin_class = getattr(sys.modules[module_name], class_name, None)
        if not inspect.isclass(plugin_class) or not issubclass(plugin_class, ImagePlugin) or inspect.isabstract(plugin_class):
            raise PluginConfigurationError(f"{class_name} must be a concrete ImagePlugin subclass")
        try:
            inspect.signature(plugin_class.apply).bind(None, None, None, [])
        except TypeError as error:
            raise PluginConfigurationError("apply must accept apply(self, image, selection, masks)") from error
        return plugin_class

    @staticmethod
    def validate_constructor(plugin_class, parameters):
        signature = inspect.signature(plugin_class)
        expected = {p.name: p for p in signature.parameters.values()}
        if any(p.kind not in (p.POSITIONAL_OR_KEYWORD, p.KEYWORD_ONLY) for p in expected.values()):
            raise PluginConfigurationError("Constructor must declare named parameters (no *args, **kwargs or positional-only arguments)")
        if set(expected) != {p.name for p in parameters}:
            raise PluginConfigurationError("YAML parameters must exactly match constructor parameter names")
        annotations = typing.get_type_hints(plugin_class.__init__)
        for parameter in parameters:
            annotation = annotations.get(parameter.name)
            if parameter.name in annotations and annotation is not TYPES[parameter.kind] and not (
                    parameter.kind in {"hsv", "color_hsv", "color_bgra"} and annotation == list[int]):
                raise PluginConfigurationError(f"{parameter.name}: constructor annotation does not match YAML type")

    @staticmethod
    def create(definition: PluginDefinition, supplied) -> ImagePlugin:
        return definition.plugin_class(**definition.bind(supplied))


class PluginRegistry:
    """Registry: expose valid definitions and isolate configuration failures per plugin."""

    def __init__(self, root: Path = PLUGIN_ROOT):
        self.root = root.resolve()
        self.definitions: dict[str, PluginDefinition] = {}
        self.errors: list[str] = []
        try:
            data = yaml.load((self.root / "plugins.yaml").read_text(encoding="utf-8"), Loader=UniqueSafeLoader)
            if not isinstance(data, dict) or set(data) != {"version", "plugins"} or type(data["version"]) is not int or data["version"] != 1:
                raise PluginConfigurationError("Expected version: 1 and plugins in plugins.yaml")
            if not isinstance(data["plugins"], list):
                raise PluginConfigurationError("plugins must be a list")
        except Exception as error:
            self.errors.append(f"plugins.yaml: {error}")
            return
        entries = []
        seen_folders = set()
        for relative in data["plugins"]:
            try:
                relative = _text(relative, "plugin directory", 1024)
                candidate = Path(relative)
                folder = (self.root / candidate).resolve()
                if candidate.is_absolute() or folder == self.root or not folder.is_relative_to(self.root):
                    raise PluginConfigurationError("Plugin directory must stay inside plugins/")
                if folder in seen_folders:
                    raise PluginConfigurationError("Duplicate plugin directory")
                seen_folders.add(folder)
                child = yaml.load((folder / "plugins.yaml").read_text(encoding="utf-8"), Loader=UniqueSafeLoader)
                if (not isinstance(child, dict) or set(child) != {"version", "plugins"}
                        or type(child["version"]) is not int or child["version"] != 1
                        or not isinstance(child["plugins"], list)):
                    raise PluginConfigurationError("Expected version: 1 and plugins list in child plugins.yaml")
                for entry in child["plugins"]:
                    if isinstance(entry, dict) and entry.get("enabled", True):
                        entry = dict(entry)
                        file = Path(_text(entry.get("file"), "file", 1024))
                        path = (folder / file).resolve()
                        if file.is_absolute() or not path.is_relative_to(folder):
                            raise PluginConfigurationError("file must stay inside its plugin directory")
                        entry["file"] = path.relative_to(self.root).as_posix()
                    entries.append(entry)
            except Exception as error:
                self.errors.append(f"{relative}/plugins.yaml: {error}")
        seen = set()
        for entry in entries:
            identity = entry.get("id", "?") if isinstance(entry, dict) else "?"
            try:
                if not isinstance(entry, dict) or set(entry) - {"id", "name", "description", "file", "class", "parameters", "enabled", "output_kind", "input_kind"}:
                    raise PluginConfigurationError("Unknown plugin fields")
                identity = _text(identity, "id", 100)
                if not re.fullmatch(r"[a-z][a-z0-9_]*", identity) or identity in seen:
                    raise PluginConfigurationError("Plugin id must be unique and match [a-z][a-z0-9_]*")
                seen.add(identity)
                if type(entry.get("enabled", True)) is not bool:
                    raise PluginConfigurationError("enabled must be a boolean")
                if not entry.get("enabled", True):
                    continue
                params = entry.get("parameters", {})
                if not isinstance(params, dict):
                    raise PluginConfigurationError("parameters must be a mapping")
                parameters = tuple(Parameter.parse(name, value) for name, value in params.items())
                by_name = {p.name: p for p in parameters}
                for parameter in parameters:
                    for reference, values in (parameter.visible_when or {}).items():
                        other = by_name.get(reference)
                        if other is None or reference == parameter.name or not other.choices or not set(values) <= set(other.choices):
                            raise PluginConfigurationError(f"{parameter.name}: invalid visible_when choice reference")
                    if parameter.palette_channel:
                        other = by_name.get(parameter.palette_channel)
                        if other is None or not other.choices or not set(other.choices) <= {"gray", "blue", "green", "red", "alpha", "hsv"}:
                            raise PluginConfigurationError(f"{parameter.name}: invalid palette channel reference")
                plugin_class = PluginFactory.load(self.root, _text(entry.get("file"), "file", 1024), _text(entry.get("class"), "class"))
                PluginFactory.validate_constructor(plugin_class, parameters)
                description = entry.get("description", "")
                if not isinstance(description, str) or len(description) > 2048:
                    raise PluginConfigurationError("Invalid plugin description")
                output_kind = entry.get("output_kind", "image")
                if output_kind not in ("image", "mask"):
                    raise PluginConfigurationError("output_kind must be image or mask")
                input_kind = entry.get("input_kind", "image")
                if input_kind not in ("image", "painting") or input_kind == "painting" and output_kind != "mask":
                    raise PluginConfigurationError("input_kind painting is reserved for mask generators")
                self.definitions[identity] = PluginDefinition(identity, _text(entry.get("name"), "name"), description, plugin_class, parameters, output_kind, input_kind)
            except Exception as error:
                self.errors.append(f"{identity}: {error}")

    def get(self, identity: str) -> PluginDefinition:
        if identity not in self.definitions:
            raise PluginConfigurationError(f"Unknown or unavailable plugin: {identity}")
        return self.definitions[identity]

    def describe(self):
        return {"plugins": [definition.describe() for definition in self.definitions.values()],
                "plugin_errors": self.errors}


@lru_cache(maxsize=1)
def get_plugin_registry() -> PluginRegistry:
    """One catalogue per backend process; Reconnect picks up changed YAML/Python."""
    return PluginRegistry()
