import json
import pathlib
import unittest

from lupa.lua51 import LuaRuntime


ROOT = pathlib.Path(__file__).resolve().parents[1]
MOD_ROOT = ROOT / "playtests" / "mods" / "starter-armory"


class StarterArmoryTests(unittest.TestCase):
    def setUp(self):
        self.runtime = LuaRuntime(unpack_returned_tuples=True)
        self.storage = {}
        self.handlers = {}
        self.added = []
        self.checked = []
        self.missing_item = None

        def get_entry(name):
            self.checked.append(name)
            return None if name == self.missing_item else {"name": name}

        def add_item(_dweller, name, amount):
            self.added.append((name, amount))

        self.runtime.globals().get_entry = get_entry
        self.runtime.globals().add_item = add_item
        source = (MOD_ROOT / "main.lua").read_text(encoding="utf-8")
        chunk = self.runtime.execute("return function()\n" + source + "\nend")
        context = self.runtime.table_from({
            "storage": self.runtime.table_from({
                "get": lambda key, default=None: self.storage.get(key, default),
                "set": lambda key, value: self.storage.__setitem__(key, value),
            }),
            "events": self.runtime.table_from({
                "on": lambda name, callback: self.handlers.__setitem__(name, callback),
            }),
            "log": lambda _message: None,
        })
        self.runtime.execute("gKosovoItemConfig = {GetEntryWithName = function(self, name) return get_entry(name) end}")
        self.runtime.execute("dweller = {AddItems = function(self, name, amount) add_item(self, name, amount) end, GetDwellerName = function() return '测试角色' end}")
        self.runtime.execute("scene = {GetDwellerCount = function() return 1 end, GetDweller = function() return dweller end}")
        entry = chunk()
        entry.on_load(context)

    def test_gives_each_new_item_once_and_thirty_rounds_of_each_ammo_type(self):
        self.handlers["game.day.begin"](self.runtime.globals().scene)
        self.assertEqual(len(self.added), 54)
        new_items = [amount for name, amount in self.added if name.startswith("MML_")]
        ammunition = [(name, amount) for name, amount in self.added if not name.startswith("MML_")]
        self.assertEqual(len(new_items), 50)
        self.assertEqual(len({name for name, amount in self.added if name.startswith("MML_")}), 50)
        self.assertTrue(all(amount == 1 for amount in new_items))
        self.assertEqual(ammunition, [
            ("PistolShells", 30),
            ("ShotgunAmmo", 30),
            ("RifleAmmo", 30),
            ("Ammo", 30),
        ])
        self.assertEqual(len(self.checked), 54)
        self.assertTrue(self.storage["granted"])

        self.handlers["game.day.begin"](self.runtime.globals().scene)
        self.assertEqual(len(self.added), 54)

    def test_missing_item_prevents_partial_grant(self):
        self.missing_item = "MML_AK74"
        with self.assertRaisesRegex(Exception, "测试物品尚未注册"):
            self.handlers["game.day.begin"](self.runtime.globals().scene)
        self.assertEqual(self.added, [])
        self.assertNotIn("granted", self.storage)

    def test_manifest_declares_all_content_dependencies_and_test_scope(self):
        manifest = json.loads((MOD_ROOT / "mod.json").read_text(encoding="utf-8"))
        self.assertEqual(manifest["id"], "twom.play.starter-armory")
        self.assertEqual(set(manifest["dependencies"]), {
            "twom.play.bridge",
            "twom.content.ammunition",
            "twom.content.field-equipment",
            "twom.content.more-guns",
            "twom.content.weapon-trading",
        })
        self.assertIn("共享物品栏", manifest["description"])
        self.assertIn("descriptionFile", manifest)
        self.assertTrue((MOD_ROOT / manifest["descriptionFile"]).is_file())


if __name__ == "__main__":
    unittest.main()
