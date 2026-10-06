import json
import pathlib
import unittest

from lupa.lua51 import LuaRuntime


ROOT = pathlib.Path(__file__).resolve().parents[1]
MOD_ROOT = ROOT / "playtests" / "mods" / "starter-armory"


class StarterArmoryTests(unittest.TestCase):
    def setUp(self):
        self.runtime = LuaRuntime(unpack_returned_tuples=True)
        self.storage = {"granted": True}
        self.handlers = {}
        self.logs = []
        self.added = []
        self.checked = []
        self.inventory = {}
        self.missing_item = None

        def get_entry(name):
            self.checked.append(name)
            return None if name == self.missing_item else {"name": name}

        def add_item(_dweller, name, amount):
            self.added.append((name, amount))
            self.inventory[name] = self.inventory.get(name, 0) + amount

        def global_count(name):
            return self.inventory.get(name, 0)

        self.runtime.globals().get_entry = get_entry
        self.runtime.globals().add_item = add_item
        source = (MOD_ROOT / "main.lua").read_text(encoding="utf-8")
        chunk = self.runtime.execute("return function()\n" + source + "\nend")
        context = self.runtime.table_from({
            "storage": self.runtime.table_from({
                "get": lambda key, default=None: self.storage.get(key, default),
                "set": lambda key, value: self.storage.__setitem__(key, value),
            }),
            "game": self.runtime.table_from({
                "inventory": self.runtime.table_from({"global_count": global_count}),
            }),
            "events": self.runtime.table_from({
                "on": lambda name, callback: self.handlers.__setitem__(name, callback),
            }),
            "log": lambda message: self.logs.append(message),
        })
        self.runtime.execute("gKosovoItemConfig = {GetEntryWithName = function(self, name) return get_entry(name) end}")
        self.runtime.execute("dweller = {AddItems = function(self, name, amount) add_item(self, name, amount) end, GetDwellerName = function() return '测试角色' end}")
        self.runtime.execute("current_day = 1; scene = {GetCurrentDay = function() return current_day end, GetDwellerCount = function() return 1 end, GetDweller = function() return dweller end}")
        entry = chunk()
        entry.on_load(context)

    def test_grants_every_fresh_campaign_once_even_when_old_install_flag_is_set(self):
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

        self.handlers["game.day.begin"](self.runtime.globals().scene)
        self.assertEqual(len(self.added), 54)

        #新战役重置库存后同一安装仍会补发全部开局物资
        self.inventory.clear()
        self.handlers["game.day.begin"](self.runtime.globals().scene)
        self.assertEqual(len(self.added), 108)

    def test_only_fills_missing_inventory_and_only_on_day_one(self):
        self.inventory.update({"MML_AK74": 1, "Ammo": 25})
        self.handlers["game.day.begin"](self.runtime.globals().scene)
        self.assertNotIn(("MML_AK74", 1), self.added)
        self.assertIn(("Ammo", 5), self.added)

        self.runtime.globals().current_day = 2
        self.inventory.clear()
        self.added.clear()
        self.handlers["game.day.begin"](self.runtime.globals().scene)
        self.assertEqual(self.added, [])

    def test_missing_item_is_reported_and_retried_without_losing_other_grants(self):
        self.missing_item = "MML_AK74"
        self.handlers["game.day.begin"](self.runtime.globals().scene)
        self.assertEqual(len(self.added), 53)
        self.assertEqual(self.inventory.get("MML_AK74", 0), 0)
        self.assertTrue(any("缺少1项" in message and "MML_AK74" in message for message in self.logs))

        #物品注册稍晚时后续昼夜事件重试缺项且库存已有项不重复添加
        self.runtime.globals().current_day = 2
        self.missing_item = None
        self.handlers["game.day.begin"](self.runtime.globals().scene)
        self.assertEqual(self.inventory.get("MML_AK74", 0), 1)
        self.assertEqual(len(self.added), 54)
        self.assertTrue(any("新战役开局物资已核验" in message for message in self.logs))

    def test_new_campaign_scene_ready_starts_grant_attempt(self):
        self.handlers["game.scene.ready"](self.runtime.globals().scene, True)
        self.assertEqual(len(self.added), 54)

    def test_unready_registry_waits_without_throwing(self):
        self.runtime.execute("gKosovoItemConfig = nil")
        self.handlers["game.day.begin"](self.runtime.globals().scene)
        self.assertEqual(self.added, [])
        self.assertTrue(any("等待原生物品配置就绪" in message for message in self.logs))

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
