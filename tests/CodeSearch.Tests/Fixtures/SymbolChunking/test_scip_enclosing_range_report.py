"""Source-boundary regressions using tiny synthetic SCIP indexes only."""

import contextlib
import io
from pathlib import Path, PureWindowsPath
import sys
import tempfile
import unittest

import scip_enclosing_range_report as report_module


def varint(value):
    encoded = bytearray()
    while value > 127:
        encoded.append((value & 127) | 128)
        value >>= 7
    encoded.append(value)
    return bytes(encoded)


def message(field, value):
    return varint((field << 3) | 2) + varint(len(value)) + value


class SourceBoundaryTests(unittest.TestCase):
    def setUp(self):
        self.temporary = tempfile.TemporaryDirectory(prefix="localai-scip-boundary-")
        self.addCleanup(self.temporary.cleanup)
        self.directory = Path(self.temporary.name)
        self.root = self.directory / "source"
        self.nested = self.root / "nested"
        self.nested.mkdir(parents=True)
        self.inside = self.nested / "inside.py"
        self.inside.write_text("inside\nsecond line\n", encoding="utf-8")
        # A common string-prefix comparison would also accept this sibling.
        self.outside_directory = self.directory / "source-neighbor"
        self.outside_directory.mkdir()
        self.outside = self.outside_directory / "outside.py"
        self.outside.write_text("outside sentinel\n", encoding="utf-8")
        self.index = self.directory / "index.scip"

    def report(self, document_path):
        # Index.documents (2), Document.relative_path (1); no external indexer.
        self.index.write_bytes(message(2, message(1, document_path.encode("utf-8"))))
        output = io.StringIO()
        with contextlib.redirect_stdout(output):
            report_module.report(self.index, self.root)
        return output.getvalue()

    def symlink(self, link, target):
        try:
            link.symlink_to(target, target_is_directory=target.is_dir())
        except OSError as error:
            if sys.platform == "win32" and getattr(error, "winerror", None) == 1314:
                self.skipTest("Windows symlink creation requires additional privileges")
            raise

    def test_nested_sources_with_either_separator_are_reported(self):
        for path in ("nested/inside.py", "nested\\inside.py"):
            with self.subTest(path=path):
                output = self.report(path)
                self.assertIn("non-blank lines no definition body covers: [1, 2]", output)
                self.assertIn("TOTAL", output)

    def test_missing_source_keeps_metadata_report(self):
        output = self.report("nested/missing.py")
        self.assertIn("nested/missing.py: 0 definitions", output)
        self.assertIn("TOTAL", output)

    def test_parent_paths_are_rejected(self):
        for path in (
            "../source-neighbor/outside.py",
            "..\\source-neighbor\\outside.py",
            "nested/../../source-neighbor/outside.py",
        ):
            with self.subTest(path=path), self.assertRaises(ValueError):
                self.report(path)

    def test_absolute_and_drive_relative_paths_are_rejected(self):
        for path in (
            str(self.inside.resolve()),
            str(self.outside.resolve()),
            "C:" + self.directory.name + "\\outside.py",
            "C:/" + self.directory.name + "/outside.py",
            "\\\\" + self.directory.name + "\\share\\outside.py",
            "/" + self.directory.name + "/outside.py",
        ):
            with self.subTest(path=path), self.assertRaises(ValueError):
                self.report(path)

    def test_empty_and_root_paths_are_rejected(self):
        for path in ("", ".", "nested/.."):
            with self.subTest(path=path), self.assertRaises(ValueError):
                self.report(path)

    def test_canonical_windows_components_preserve_case_boundaries(self):
        # Windows path objects fold case even when NTFS treats these as siblings.
        root = PureWindowsPath("C:/fixture/Source")
        self.assertTrue(report_module.is_source_child(
            PureWindowsPath("C:/fixture/Source/nested/inside.py"), root))
        for source in (
            PureWindowsPath("C:/fixture/source/outside.py"),
            PureWindowsPath("C:/fixture/Source-neighbor/outside.py"),
            root,
        ):
            with self.subTest(source=str(source)):
                self.assertFalse(report_module.is_source_child(source, root))

    def test_file_symlink_escape_is_rejected(self):
        self.symlink(self.root / "linked.py", self.outside)
        with self.assertRaises(ValueError):
            self.report("linked.py")

    def test_directory_symlink_escape_is_rejected(self):
        self.symlink(self.root / "linked", self.outside_directory)
        with self.assertRaises(ValueError):
            self.report("linked/outside.py")

    def test_inside_symlink_is_reported(self):
        self.symlink(self.root / "linked.py", self.inside)
        output = self.report("linked.py")
        self.assertIn("non-blank lines no definition body covers: [1, 2]", output)


if __name__ == "__main__":
    unittest.main()
