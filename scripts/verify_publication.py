"""Check static publication links and the actual precomputed demo results."""
import argparse
from html.parser import HTMLParser
import json
from pathlib import Path
import re
import sys
from urllib.parse import urlsplit, unquote
import xml.etree.ElementTree as ET

ROOT = Path(__file__).resolve().parents[1]
SITE = ROOT / "docs" / "site"


class Page(HTMLParser):
    def __init__(self):
        super().__init__()
        self.links = []
        self.ids = set()
        self.metas = {}
        self.canonical = None
        self.in_schema = False
        self.schema = ""
        self.lang = None
        self.title = False

    def handle_starttag(self, tag, attrs):
        values = dict(attrs)
        if tag == "html":
            self.lang = values.get("lang")
        if tag == "title":
            self.title = True
        if values.get("id"):
            self.ids.add(values["id"])
        for key in ["href", "src"]:
            if values.get(key):
                self.links.append(values[key])
        if tag == "meta":
            self.metas[values.get("name", values.get("property"))] = values.get("content")
        if tag == "link" and values.get("rel") == "canonical":
            self.canonical = values.get("href")
        if tag == "script" and values.get("type") == "application/ld+json":
            self.in_schema = True

    def handle_endtag(self, tag):
        if tag == "script":
            self.in_schema = False

    def handle_data(self, data):
        if self.in_schema:
            self.schema += data


def static_checks():
    page = Page()
    html = (SITE / "index.html").read_text(encoding="utf-8")
    page.feed(html)
    assert page.lang == "fr" and page.title
    assert page.metas["description"] and "index" in page.metas["robots"]
    assert page.canonical and page.canonical.startswith("https://") and page.canonical.endswith("/")
    schema = json.loads(page.schema)
    assert schema["@type"] == "SoftwareApplication" and schema["url"] == page.canonical
    assert page.metas["og:url"] == page.canonical
    assert page.metas["og:image"] == page.canonical + "assets/social-preview.png"
    tree = ET.parse(SITE / "sitemap.xml")
    assert tree.find(".//{http://www.sitemaps.org/schemas/sitemap/0.9}loc").text == page.canonical
    for link in page.links:
        parsed = urlsplit(link)
        if parsed.scheme or parsed.netloc:
            continue
        if parsed.path:
            target = (SITE / unquote(parsed.path)).resolve()
            assert target.is_relative_to(SITE.resolve()) and target.exists(), link
        if parsed.fragment and not parsed.path:
            assert parsed.fragment in page.ids, link
    markdown_files = [ROOT / "README.md", ROOT / "README.en.md", ROOT / "CONTRIBUTING.md",
                      ROOT / "backend" / "README.md"]
    markdown_files += sorted((ROOT / "docs").rglob("*.md"))
    markdown_files += sorted((ROOT / "FURTHER").rglob("*.md"))
    for file in markdown_files:
        content = file.read_text(encoding="utf-8")
        assert not re.search(r"__(?:SITE|OWNER|REPOSITORY|LICENSE_LINE(?:_EN)?)__", content), file
        for link in re.findall(r"\]\(([^)]+)\)", content):
            parsed = urlsplit(link)
            if not parsed.scheme and not parsed.netloc:
                target = (file.parent / unquote(parsed.path)).resolve() if parsed.path else file.resolve()
                assert target.is_relative_to(ROOT.resolve()) and target.exists(), (file, link)
                if parsed.fragment and target.suffix == ".md":
                    counts = {}
                    ids = set()
                    for heading in re.findall(r"^#{1,6} (.+)$", target.read_text(encoding="utf-8"), re.M):
                        slug = re.sub(r"[^\w -]", "", heading.lower()).replace(" ", "-")
                        count = counts.get(slug, 0)
                        counts[slug] = count + 1
                        ids.add(slug if count == 0 else slug + "-" + str(count))
                    assert unquote(parsed.fragment) in ids, (file, link, "missing heading")
    assert not re.search(r"__(?:SITE|OWNER|REPOSITORY)__", html)
    for asset in ["original.png", "denoise.png", "grayscale.png", "social-preview.png", "workspace.png", "logo.svg", "demo.json"]:
        assert (SITE / "assets" / asset).is_file(), asset
    print("Static site, sitemap, schema and documentation links verified.")


def processing_checks():
    sys.path.insert(0, str(ROOT / "scripts"))
    import cv2
    import numpy as np
    from build_demo import process, scene, PARAMETERS
    assets = SITE / "assets"
    original = cv2.imread(str(assets / "original.png"), cv2.IMREAD_UNCHANGED)
    np.testing.assert_array_equal(original, scene())
    actual_denoise, actual_gray = process(original)
    expected_denoise = cv2.imread(str(assets / "denoise.png"), cv2.IMREAD_UNCHANGED)
    expected_gray = cv2.imread(str(assets / "grayscale.png"), cv2.IMREAD_UNCHANGED)
    np.testing.assert_array_equal(expected_gray, actual_gray)
    # OpenCV SIMD builds can differ by one integer level across machines.
    delta = np.abs(expected_denoise.astype(np.int16) - actual_denoise.astype(np.int16))
    assert delta.max() <= 1, f"Denoise asset differs from project processing: {delta.max()}"
    np.testing.assert_array_equal(expected_denoise[:, :, 3], original[:, :, 3])
    outside = np.ones(original.shape[:2], dtype=bool)
    outside[70:530, 80:880] = False
    np.testing.assert_array_equal(expected_denoise[outside], original[outside])
    meta = json.loads((assets / "demo.json").read_text(encoding="utf-8"))
    assert meta["parameters"] == PARAMETERS
    social = cv2.imread(str(assets / "social-preview.png"))
    assert social.shape == (630, 1200, 3)
    print("Precomputed demo matches real processing; original, alpha and selection verified.")


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--static-only", action="store_true")
    args = parser.parse_args()
    static_checks()
    if not args.static_only:
        processing_checks()


if __name__ == "__main__":
    main()
