import pathlib
import subprocess
import sys
import tempfile
import unittest


class LauncherIsolationTest(unittest.TestCase):
    def launch(self, settings):
        with tempfile.TemporaryDirectory(prefix="stagehand-launch-test-") as temporary:
            root = pathlib.Path(temporary)
            macos = root / "SlayTheSpire2.app/Contents/MacOS"
            macos.mkdir(parents=True)
            executable = macos / "Slay the Spire 2"
            executable.write_text("#!/bin/sh\nexit 0\n")
            executable.chmod(0o700)
            (macos / "override.cfg").write_text(settings)
            artifacts = root / "artifacts"
            artifacts.mkdir()
            for name in ("RuntimeControl.dll", "RuntimeControl.pck"):
                (artifacts / name).write_bytes(b"test artifact")
            result = subprocess.run(
                [sys.executable, str(pathlib.Path(__file__).parents[1] / "launch-isolated.py"),
                 "--directory", str(root), "--artifacts", str(artifacts)],
                capture_output=True, text=True, timeout=5,
            )
            return result.returncode, (macos / "mods").exists(), (root / "session.json").exists()

    def test_effective_userdir_must_be_enabled_before_deployment(self):
        for settings in (
            '[application]\nconfig/use_custom_user_dir=false\nconfig/custom_user_dir_name="stagehand-sts2-test"\n',
            '; stagehand-sts2-stale-comment\n[application]\n',
            '[application]\n; config/use_custom_user_dir=true\nconfig/custom_user_dir_name="stagehand-sts2-test"\n',
            '[application]\nconfig/use_custom_user_dir=true\nconfig/custom_user_dir_name="ordinary-profile"\n',
            '[application]\nconfig/use_custom_user_dir=true\nconfig/custom_user_dir_name="stagehand-sts2-test/../../ordinary"\n',
            '[DEFAULT]\nconfig/use_custom_user_dir=true\nconfig/custom_user_dir_name="stagehand-sts2-test"\n[application]\n',
            '[application]\nconfig/use_custom_user_dir: true\nconfig/custom_user_dir_name: "stagehand-sts2-test"\n',
        ):
            with self.subTest(settings=settings):
                code, deployed, session = self.launch(settings)
                self.assertNotEqual(code, 0)
                self.assertFalse(deployed)
                self.assertFalse(session)

    def test_valid_explicit_userdir_launches_only_fixture_executable(self):
        code, deployed, session = self.launch(
            '[application]\nconfig/use_custom_user_dir=true\n'
            'config/custom_user_dir_name="stagehand-sts2-test" ; isolated profile\n'
        )
        self.assertEqual(code, 0)
        self.assertTrue(deployed)
        self.assertTrue(session)
