namespace TSM.Models;

public sealed record AuctionItemClassification(int Quality, string QualityName, string Category, string Subcategory);

public static class WowItemTaxonomy
{
    private static readonly IReadOnlyDictionary<int, string> Categories = new Dictionary<int, string>
    {
        [0] = "Consumable", [1] = "Container", [2] = "Weapon", [3] = "Gem", [4] = "Armor",
        [5] = "Reagent", [6] = "Projectile", [7] = "Trade Goods", [8] = "Item Enhancement",
        [9] = "Recipe", [10] = "Money", [11] = "Quiver", [12] = "Quest Item", [13] = "Key",
        [14] = "Permanent", [15] = "Miscellaneous", [16] = "Glyph", [17] = "Battle Pet",
        [18] = "WoW Token"
    };

    private static readonly IReadOnlyDictionary<int, IReadOnlyDictionary<int, string>> Subcategories =
        new Dictionary<int, IReadOnlyDictionary<int, string>>
        {
            [0] = Map("Consumable", "Potion", "Elixir", "Flask", "Scroll", "Food & Drink", "Item Enhancement",
                "Bandage", "Other", "Vantus Rune"),
            [1] = Map("Bag", "Soul Bag", "Herb Bag", "Enchanting Bag", "Engineering Bag", "Gem Bag", "Mining Bag",
                "Leatherworking Bag", "Inscription Bag", "Tackle Box", "Cooking Bag"),
            [2] = Map("One-Handed Axe", "Two-Handed Axe", "Bow", "Gun", "One-Handed Mace", "Two-Handed Mace",
                "Polearm", "One-Handed Sword", "Two-Handed Sword", "Warglaive", "Staff", "Bear Claw", "Cat Claw",
                "Fist Weapon", "Miscellaneous", "Dagger", "Thrown", "Spear", "Crossbow", "Wand", "Fishing Pole"),
            [3] = Map("Intellect", "Agility", "Strength", "Stamina", "Spirit", "Critical Strike", "Mastery",
                "Haste", "Versatility", "Other", "Multiple Stats", "Artifact Relic"),
            [4] = Map("Miscellaneous", "Cloth", "Leather", "Mail", "Plate", "Cosmetic", "Shield", "Libram",
                "Idol", "Totem", "Sigil", "Relic"),
            [6] = Map("Wand", "Bolt", "Arrow", "Bullet", "Thrown"),
            [7] = Map("Parts", "Explosives", "Devices", "Jewelcrafting", "Cloth", "Leather", "Metal & Stone",
                "Cooking", "Herb", "Elemental", "Other", "Enchanting", "Materials", "Item Enchantment",
                "Optional Reagents", "Finishing Reagents"),
            [8] = Map("Head", "Neck", "Shoulder", "Cloak", "Chest", "Wrist", "Hands", "Waist", "Legs", "Feet",
                "Finger", "Weapon", "Two-Handed Weapon", "Shield/Off-hand", "Miscellaneous"),
            [9] = Map("Book", "Leatherworking", "Tailoring", "Engineering", "Blacksmithing", "Cooking", "Alchemy",
                "First Aid", "Enchanting", "Fishing", "Jewelcrafting", "Inscription"),
            [11] = Map("Quiver", "Ammo Pouch"),
            [15] = Map("Junk", "Reagent", "Companion Pet", "Holiday", "Other", "Mount"),
            [16] = Map("Warrior", "Paladin", "Hunter", "Rogue", "Priest", "Death Knight", "Shaman", "Mage",
                "Warlock", "Monk", "Druid", "Demon Hunter", "Evoker")
        };

    public static IReadOnlyList<string> CategoryOptions { get; } =
        ["All categories", .. Categories.Values.Order(StringComparer.OrdinalIgnoreCase)];

    public static IReadOnlyList<string> QualityOptions { get; } =
        ["All qualities", "Poor", "Common", "Uncommon", "Rare", "Epic", "Legendary", "Artifact", "Heirloom"];

    private static readonly IReadOnlyDictionary<string, string> InventoryTypes =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["Ammo"] = "AMMO", ["Back"] = "CLOAK", ["Bag"] = "BAG", ["Chest"] = "CHEST|ROBE",
            ["Feet"] = "FEET", ["Finger"] = "FINGER", ["Hands"] = "HAND", ["Head"] = "HEAD",
            ["Held in off-hand"] = "HOLDABLE", ["Legs"] = "LEGS", ["Main hand"] = "WEAPONMAINHAND",
            ["Neck"] = "NECK", ["Off hand"] = "WEAPONOFFHAND", ["One-hand"] = "WEAPON",
            ["Ranged"] = "RANGED|RANGEDRIGHT", ["Relic"] = "RELIC", ["Shield"] = "SHIELD",
            ["Shirt"] = "BODY", ["Shoulder"] = "SHOULDER", ["Tabard"] = "TABARD",
            ["Thrown"] = "THROWN", ["Trinket"] = "TRINKET", ["Two-hand"] = "TWOHWEAPON",
            ["Waist"] = "WAIST", ["Wrist"] = "WRIST"
        };

    public static IReadOnlyList<string> InventoryTypeOptions { get; } =
        ["All slots", .. InventoryTypes.Keys.Order(StringComparer.OrdinalIgnoreCase)];

    public static AuctionItemClassification Classify(int quality, int classId, int subclassId)
    {
        var category = Categories.GetValueOrDefault(classId, $"Category {classId}");
        var subcategory = Subcategories.TryGetValue(classId, out var values)
            ? values.GetValueOrDefault(subclassId, $"Other ({subclassId})")
            : category;
        return new AuctionItemClassification(quality, QualityName(quality), category, subcategory);
    }

    public static IReadOnlyList<string> SubcategoryOptions(string category)
    {
        if (string.Equals(category, "All categories", StringComparison.OrdinalIgnoreCase))
            return ["All subcategories"];
        var classId = Categories.FirstOrDefault(pair => string.Equals(pair.Value, category,
            StringComparison.OrdinalIgnoreCase)).Key;
        return Subcategories.TryGetValue(classId, out var values)
            ? ["All subcategories", .. values.Values.Distinct(StringComparer.OrdinalIgnoreCase)]
            : ["All subcategories"];
    }

    public static int? CategoryId(string category) => Categories
        .Where(pair => string.Equals(pair.Value, category, StringComparison.OrdinalIgnoreCase))
        .Select(pair => (int?)pair.Key).FirstOrDefault();

    public static int? SubcategoryId(string category, string subcategory)
    {
        var classId = CategoryId(category);
        return classId is { } value && Subcategories.TryGetValue(value, out var subcategories)
            ? subcategories.Where(pair => string.Equals(pair.Value, subcategory, StringComparison.OrdinalIgnoreCase))
                .Select(pair => (int?)pair.Key).FirstOrDefault()
            : null;
    }

    public static int? QualityId(string quality)
    {
        var index = QualityOptions.Select((value, position) => (value, position))
            .Where(option => string.Equals(option.value, quality, StringComparison.OrdinalIgnoreCase))
            .Select(option => option.position).DefaultIfEmpty(-1).First();
        return index > 0 ? index - 1 : null;
    }

    public static string InventoryType(string slot) => InventoryTypes.GetValueOrDefault(slot, string.Empty);

    private static string QualityName(int quality) => quality switch
    {
        0 => "Poor", 1 => "Common", 2 => "Uncommon", 3 => "Rare", 4 => "Epic", 5 => "Legendary",
        6 => "Artifact", 7 => "Heirloom", _ => "Unknown"
    };

    private static IReadOnlyDictionary<int, string> Map(params string[] values) =>
        values.Select((value, index) => (value, index)).ToDictionary(pair => pair.index, pair => pair.value);
}
