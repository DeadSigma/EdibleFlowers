using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Reflection;
using HarmonyLib;
using RaftModLoader;
using HMLLibrary;
using UnityEngine;
using UnityEngine.SceneManagement;

public class EdibleFlowers : Mod
{
    private const string PatchId = "el.raft.edibleflowers";
    private const string BundleName = "edibleflowers.assets";
    private const string PresetManifestName = "transforms/index.txt";

    private static readonly FlowerFood[] FlowerFoods =
    {
        new FlowerFood("Flower_Black", 12.5f, 15f),
        new FlowerFood("Flower_Blue", 12.5f, 15f),
        new FlowerFood("Flower_Red", 17.5f, 21f),
        new FlowerFood("Flower_White", 12.5f, 15f),
        new FlowerFood("Flower_Yellow", 15f, 18f)
    };

    private static readonly Dictionary<Item_Base, OriginalSettings> Originals =
        new Dictionary<Item_Base, OriginalSettings>();

    private static readonly FieldInfo HungerYieldField =
        AccessTools.Field(typeof(ItemInstance_Consumeable), "hungerYield");
    private static readonly FieldInfo BonusHungerYieldField =
        AccessTools.Field(typeof(ItemInstance_Consumeable), "bonusHungerYield");
    private static readonly FieldInfo ThirstYieldField =
        AccessTools.Field(typeof(ItemInstance_Consumeable), "thirstYield");
    private static readonly FieldInfo BonusThirstYieldField =
        AccessTools.Field(typeof(ItemInstance_Consumeable), "bonusThirstYield");
    private static readonly FieldInfo IsRawField =
        AccessTools.Field(typeof(ItemInstance_Consumeable), "isRaw");
    private static readonly FieldInfo ConsumeSoundField =
        AccessTools.Field(typeof(ItemInstance_Consumeable), "eventRef_consumeSound");

    private static readonly FieldInfo ConnectionDictionaryField =
        AccessTools.Field(typeof(UseItemController), "connectionDictionary");
    private static readonly FieldInfo ArmMeshField =
        AccessTools.Field(typeof(CharacterModelModifications), "armMesh");

    private static bool applying;
    private Harmony harmony;
    private AssetBundle flowerBundle;
    private readonly Dictionary<string, GameObject> handPrefabs =
        new Dictionary<string, GameObject>(StringComparer.Ordinal);
    private readonly Dictionary<string, List<FlowerPose>> flowerPoses =
        new Dictionary<string, List<FlowerPose>>(StringComparer.Ordinal);
    private int boundScene = -1;
    private float nextRegistrationAttempt;
    private float nextLayerCheck;
    private bool missingHandLayerWarned;
    private readonly Dictionary<GameObject, LayerSnapshot> flowerLayers =
        new Dictionary<GameObject, LayerSnapshot>();

    private sealed class LayerSnapshot
    {
        public readonly Dictionary<GameObject, int> Original = new Dictionary<GameObject, int>();
        public bool IsInHandLayer;

        public LayerSnapshot(GameObject root)
        {
            foreach (Transform child in root.GetComponentsInChildren<Transform>(true))
                Original[child.gameObject] = child.gameObject.layer;
        }

        public void SetLayer(int layer)
        {
            foreach (GameObject obj in Original.Keys)
                if (obj != null) obj.layer = layer;
            IsInHandLayer = true;
        }

        public void Restore()
        {
            foreach (KeyValuePair<GameObject, int> entry in Original)
                if (entry.Key != null) entry.Key.layer = entry.Value;
            IsInHandLayer = false;
        }
    }

    public void Start()
    {
        harmony = new Harmony(PatchId);
        harmony.PatchAll(Assembly.GetExecutingAssembly());
        ApplyFlowerSettings();
        LoadFlowerPresets();
        LoadFlowerAssets();
        Debug.Log("[EdibleFlowers] Flower food initialized");
    }

    private sealed class FlowerPose
    {
        public string ItemName;
        public string TargetName;
        public Vector3 Position;
        public Vector3 Rotation;
        public Vector3 Scale;
    }

    private void LoadFlowerPresets()
    {
        try
        {
            byte[] manifest = GetEmbeddedFileBytes(PresetManifestName);
            if (manifest == null || manifest.Length == 0) return;

            string[] files = Encoding.UTF8.GetString(manifest).TrimStart('\uFEFF')
                .Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);

            foreach (string entry in files)
            {
                string filename = entry.Trim();
                if (filename.Length == 0 || filename.StartsWith("#", StringComparison.Ordinal)) continue;
                if (!filename.EndsWith(".ini", StringComparison.OrdinalIgnoreCase) ||
                    filename.IndexOf('/') >= 0 || filename.IndexOf('\\') >= 0 ||
                    filename.IndexOf("..", StringComparison.Ordinal) >= 0)
                {
                    Debug.LogWarning("[EdibleFlowers] Invalid preset filename: " + filename);
                    continue;
                }

                try
                {
                    byte[] data = GetEmbeddedFileBytes("transforms/" + filename);
                    if (data == null || data.Length == 0) continue;
                    FlowerPose pose = ParseFlowerPose(Encoding.UTF8.GetString(data).TrimStart('\uFEFF'));
                    if (!IsFlowerName(pose.ItemName))
                    {
                        Debug.LogWarning("[EdibleFlowers] Unsupported preset item: " + pose.ItemName);
                        continue;
                    }

                    List<FlowerPose> poses;
                    if (!flowerPoses.TryGetValue(pose.ItemName, out poses))
                    {
                        poses = new List<FlowerPose>();
                        flowerPoses.Add(pose.ItemName, poses);
                    }
                    poses.Add(pose);
                }
                catch (Exception ex)
                {
                    Debug.LogWarning("[EdibleFlowers] Invalid preset " + filename + ": " + ex.Message);
                }
            }
        }
        catch (Exception ex)
        {
            Debug.LogWarning("[EdibleFlowers] Unable to load preset index: " + ex.Message);
        }
    }

    private static FlowerPose ParseFlowerPose(string text)
    {
        Dictionary<string, string> values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (string line in text.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
        {
            int separator = line.IndexOf('=');
            if (separator <= 0) continue;
            values[line.Substring(0, separator).Trim()] = line.Substring(separator + 1).Trim();
        }

        string item;
        string target;
        if (!values.TryGetValue("item", out item) || string.IsNullOrWhiteSpace(item) ||
            !values.TryGetValue("target", out target) || string.IsNullOrWhiteSpace(target))
            throw new FormatException("item or target is missing");

        return new FlowerPose
        {
            ItemName = item,
            TargetName = target,
            Position = new Vector3(ReadPresetFloat(values, "px"), ReadPresetFloat(values, "py"), ReadPresetFloat(values, "pz")),
            Rotation = new Vector3(ReadPresetFloat(values, "rx"), ReadPresetFloat(values, "ry"), ReadPresetFloat(values, "rz")),
            Scale = new Vector3(ReadPresetFloat(values, "sx"), ReadPresetFloat(values, "sy"), ReadPresetFloat(values, "sz"))
        };
    }

    private static float ReadPresetFloat(Dictionary<string, string> values, string key)
    {
        string raw;
        float value;
        if (!values.TryGetValue(key, out raw) ||
            !float.TryParse(raw.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out value) ||
            float.IsNaN(value) || float.IsInfinity(value))
            throw new FormatException("invalid preset value: " + key);
        return value;
    }

    private void ApplyFlowerPresets(GameObject prefab, string itemName)
    {
        List<FlowerPose> poses;
        if (!flowerPoses.TryGetValue(itemName, out poses)) return;

        foreach (FlowerPose pose in poses)
        {
            Transform target = prefab.transform.Find(pose.TargetName);
            if (target == null)
            {
                foreach (Transform candidate in prefab.GetComponentsInChildren<Transform>(true))
                {
                    if (candidate != prefab.transform && candidate.name == pose.TargetName)
                    {
                        target = candidate;
                        break;
                    }
                }
            }
            if (target == null)
            {
                Debug.LogWarning("[EdibleFlowers] Preset target not found: " + itemName + "/" + pose.TargetName);
                continue;
            }
            if (pose.Scale.x <= 0f || pose.Scale.y <= 0f || pose.Scale.z <= 0f)
            {
                Debug.LogWarning("[EdibleFlowers] Invalid preset scale for " + itemName);
                continue;
            }

            target.localPosition = pose.Position;
            target.localRotation = Quaternion.Euler(pose.Rotation);
            target.localScale = pose.Scale;
            Debug.Log("[EdibleFlowers] Applied pose: " + itemName + "/" + pose.TargetName);
        }
    }

    private void LoadFlowerAssets()
    {
        try
        {
            byte[] bytes = GetEmbeddedFileBytes(BundleName);
            if (bytes == null || bytes.Length == 0)
            {
                Debug.LogWarning("[EdibleFlowers] Missing edibleflowers.assets in mod root - food remains available");
                return;
            }

            flowerBundle = AssetBundle.LoadFromMemory(bytes);
            if (flowerBundle == null)
            {
                Debug.LogError("[EdibleFlowers] AssetBundle load failed");
                return;
            }

            foreach (FlowerFood food in FlowerFoods)
            {
                GameObject prefab = flowerBundle.LoadAsset<GameObject>(food.ItemName);
                if (prefab == null)
                {
                    string assetPath = "assets/edibleflowers/prefabs/" + food.ItemName.ToLowerInvariant() + ".prefab";
                    prefab = flowerBundle.LoadAsset<GameObject>(assetPath);
                }
                if (prefab == null)
                {
                    Debug.LogWarning("[EdibleFlowers] Prefab not found: " + food.ItemName);
                    continue;
                }
                ApplyFlowerPresets(prefab, food.ItemName);
                handPrefabs[food.ItemName] = prefab;
            }

            Debug.Log("[EdibleFlowers] Hand prefabs loaded: " + handPrefabs.Count);
        }
        catch (Exception error)
        {
            Debug.LogError("[EdibleFlowers] Unable to load hand prefabs: " + error);
        }
    }

    public void Update()
    {
        if (handPrefabs.Count == 0)
        {
            return;
        }

        if (!RAPI.IsCurrentSceneGame())
        {
            RestoreFlowerLayers();
            flowerLayers.Clear();
            boundScene = -1;
            return;
        }

        int scene = SceneManager.GetActiveScene().handle;
        UpdateFlowerLayers();
        if (scene == boundScene || Time.unscaledTime < nextRegistrationAttempt)
        {
            return;
        }

        nextRegistrationAttempt = Time.unscaledTime + 2f;

        try
        {
            ApplyFlowerSettings();
            int count = 0;
            foreach (FlowerFood food in FlowerFoods)
            {
                GameObject prefab;
                if (!handPrefabs.TryGetValue(food.ItemName, out prefab))
                {
                    continue;
                }

                Item_Base item = ItemManager.GetItemByName(food.ItemName);
                if (item == null)
                {
                    continue;
                }

                RAPI.SetItemObject(item, prefab);
                count++;
            }

            if (count > 0)
            {
                boundScene = scene;
                Debug.Log("[EdibleFlowers] Hand models registered: " + count);
            }
        }
        catch (Exception error)
        {
            Debug.LogWarning("[EdibleFlowers] Hand model registration will retry: " + error.Message);
        }
    }

    private static bool IsFlowerName(string name)
    {
        foreach (FlowerFood flower in FlowerFoods)
            if (string.Equals(name, flower.ItemName, StringComparison.Ordinal)) return true;
        return false;
    }

    private static UseItemController FindItemController(Network_Player player)
    {
        if (player == null || player.PlayerItemManager == null) return null;
        UseItemController controller = player.PlayerItemManager.GetComponent<UseItemController>();
        if (controller == null)
            controller = player.PlayerItemManager.GetComponentInChildren<UseItemController>(true);
        return controller;
    }

    private static int ChooseHandLayer(Network_Player player, UseItemController controller)
    {
        if (player.HandCamera == null || player.Camera == null) return -1;

        // Слой берётся с самой модели рук, а не вычисляется по маскам наугад
        Renderer armRenderer = ArmMeshField != null && player.currentModel != null
            ? ArmMeshField.GetValue(player.currentModel) as Renderer
            : null;
        if (armRenderer != null)
        {
            int layer = armRenderer.gameObject.layer;
            if ((player.HandCamera.cullingMask & (1 << layer)) != 0)
                return layer;
        }
        int handMask = player.HandCamera.cullingMask;
        int exclusive = handMask & ~player.Camera.cullingMask;
        if (exclusive == 0) return -1;

        // Слой определяется по ванильным моделям, видимым только камерой рук
        int[] counts = new int[32];
        if (controller != null && controller.allConnections != null)
        {
            foreach (ItemConnection connection in controller.allConnections)
            {
                if (connection == null || connection.obj == null || connection.inventoryItem == null ||
                    IsFlowerName(connection.inventoryItem.UniqueName)) continue;
                Renderer[] models = connection.obj.GetComponentsInChildren<Renderer>(true);
                foreach (Renderer renderer in models)
                {
                    if (renderer == null) continue;
                    int layer = renderer.gameObject.layer;
                    if ((exclusive & (1 << layer)) != 0) counts[layer]++;
                }
            }
        }

        int preferred = -1;
        int bestCount = 0;
        for (int layer = 0; layer < 32; layer++)
        {
            if (counts[layer] > bestCount)
            {
                bestCount = counts[layer];
                preferred = layer;
            }
        }
        if (preferred >= 0) return preferred;

        // Если слой у камеры рук один, неоднозначности нет
        if ((exclusive & (exclusive - 1)) == 0)
        {
            for (int layer = 0; layer < 32; layer++)
                if ((exclusive & (1 << layer)) != 0) return layer;
        }
        return -1;
    }

    private void RestoreFlowerLayers()
    {
        foreach (LayerSnapshot snapshot in flowerLayers.Values)
            if (snapshot.IsInHandLayer) snapshot.Restore();
    }

    private static GameObject FindRegisteredFlower(UseItemController controller, string itemName)
    {
        if (controller == null || ConnectionDictionaryField == null || string.IsNullOrEmpty(itemName))
            return null;

        // RML регистрирует новые модели только в connectionDictionary
        Dictionary<string, ItemConnection> registered =
            ConnectionDictionaryField.GetValue(controller) as Dictionary<string, ItemConnection>;
        if (registered == null) return null;

        ItemConnection connection;
        if (!registered.TryGetValue(itemName, out connection) || connection == null)
            return null;
        return connection.obj != null && connection.obj.activeInHierarchy ? connection.obj : null;
    }

    private void UpdateFlowerLayers()
    {
        if (Time.unscaledTime < nextLayerCheck) return;
        nextLayerCheck = Time.unscaledTime + 0.15f;

        try
        {
            Network_Player player = RAPI.GetLocalPlayer();
            if (player == null || player.currentModel == null) return;
            if (player.currentModel.thirdPersonSettings.ThirdPersonState)
            {
                RestoreFlowerLayers();
                return;
            }

            UseItemController controller = FindItemController(player);
            if (controller == null) return;
            Item_Base held = controller.GetCurrentItemInHand();
            if (held == null || !IsFlowerName(held.UniqueName)) return;

            GameObject model = FindRegisteredFlower(controller, held.UniqueName);
            if (model == null) return;

            int handLayer = ChooseHandLayer(player, controller);
            if (handLayer < 0)
            {
                if (!missingHandLayerWarned)
                {
                    missingHandLayerWarned = true;
                    Debug.LogWarning("[EdibleFlowers] Could not determine separate hand camera layer - hand mask " +
                        (player.HandCamera != null ? player.HandCamera.cullingMask.ToString() : "null") +
                        ", main mask " + (player.Camera != null ? player.Camera.cullingMask.ToString() : "null"));
                }
                return;
            }

            LayerSnapshot snapshot;
            if (!flowerLayers.TryGetValue(model, out snapshot))
            {
                snapshot = new LayerSnapshot(model);
                flowerLayers.Add(model, snapshot);
            }
            if (!snapshot.IsInHandLayer)
            {
                snapshot.SetLayer(handLayer);
                Debug.Log("[EdibleFlowers] First-person model " + held.UniqueName +
                    " assigned to hand camera layer " + LayerMask.LayerToName(handLayer) + " (" + handLayer + ")" +
                    ", mainMask=" + player.Camera.cullingMask +
                    ", handMask=" + player.HandCamera.cullingMask);
            }
        }
        catch (Exception ex)
        {
            Debug.LogWarning("[EdibleFlowers] Hand rendering fix: " + ex.Message);
        }
    }

    public void OnModUnload()
    {
        if (harmony != null)
        {
            harmony.UnpatchAll(harmony.Id);
        }

        foreach (KeyValuePair<Item_Base, OriginalSettings> entry in Originals)
        {
            if (entry.Key == null)
            {
                continue;
            }

            entry.Key.settings_consumeable = entry.Value.Consumeable;
            entry.Key.settings_usable = entry.Value.Usable;
        }

        Originals.Clear();
        RestoreFlowerLayers();
        flowerLayers.Clear();
        handPrefabs.Clear();
        flowerPoses.Clear();
        boundScene = -1;

        if (flowerBundle != null)
        {
            flowerBundle.Unload(false);
            flowerBundle = null;
        }

        Destroy(gameObject);
    }

    internal static void ApplyFlowerSettings()
    {
        if (applying)
        {
            return;
        }

        applying = true;
        try
        {
            Item_Base foodTemplate = ItemManager.GetItemByName("Berries_Red");
            if (foodTemplate == null || foodTemplate.settings_consumeable == null ||
                foodTemplate.settings_usable == null)
            {
                Debug.LogWarning("[EdibleFlowers] Berries_Red food template unavailable");
                return;
            }

            string consumeSound = FindVanillaEatingSound();
            if (string.IsNullOrEmpty(consumeSound))
            {
                Debug.LogWarning("[EdibleFlowers] No vanilla eating FMOD event found");
            }

            foreach (FlowerFood flowerFood in FlowerFoods)
            {
                Item_Base flower = ItemManager.GetItemByName(flowerFood.ItemName);
                if (flower == null || Originals.ContainsKey(flower))
                {
                    continue;
                }

                ItemInstance_Consumeable consumeable = foodTemplate.settings_consumeable.Clone();
                ItemInstance_Usable templateUsable = foodTemplate.settings_usable;
                string useButton = templateUsable.GetUseButtonName();
                if (string.IsNullOrEmpty(useButton)) useButton = "LeftClick";

                ItemInstance_Usable usable = new ItemInstance_Usable(
                    useButton,
                    Mathf.Max(0.2f, templateUsable.UseButtonCooldown),
                    1,
                    true,
                    false,
                    templateUsable.AnimationOnSelect,
                    templateUsable.AnimationOnUse,
                    templateUsable.ForceAnimationIndex,
                    templateUsable.SetTriggering,
                    templateUsable.LockItemDuringCooldown,
                    templateUsable.ResetTriggerOnDeselect);

                HungerYieldField.SetValue(consumeable, flowerFood.Hunger);
                BonusHungerYieldField.SetValue(consumeable, 0f);
                ThirstYieldField.SetValue(consumeable, flowerFood.Thirst);
                BonusThirstYieldField.SetValue(consumeable, 0f);
                IsRawField.SetValue(consumeable, true);
                if (!string.IsNullOrEmpty(consumeSound) && ConsumeSoundField != null)
                {
                    ConsumeSoundField.SetValue(consumeable, consumeSound);
                }

                Originals.Add(flower, new OriginalSettings(
                    flower.settings_consumeable, flower.settings_usable));

                flower.settings_consumeable = consumeable;
                flower.settings_usable = usable;
            }
        }
        catch (Exception error)
        {
            Debug.LogError("[EdibleFlowers] Unable to apply food settings: " + error);
        }
        finally
        {
            applying = false;
        }
    }

    private static string FindVanillaEatingSound()
    {
        string[] templateNames =
        {
            "Banana", "Raw_Beet", "Raw_Potato", "Coconut", "Berries_Red", "Strawberry"
        };

        foreach (string name in templateNames)
        {
            Item_Base item = ItemManager.GetItemByName(name);
            if (item == null || item.settings_consumeable == null)
            {
                continue;
            }

            string sound = item.settings_consumeable.EventRef_ConsumeSound;
            if (!string.IsNullOrWhiteSpace(sound))
            {
                return sound;
            }
        }

        return null;
    }

    private sealed class FlowerFood
    {
        public readonly string ItemName;
        public readonly float Hunger;
        public readonly float Thirst;

        public FlowerFood(string itemName, float hunger, float thirst)
        {
            ItemName = itemName;
            Hunger = hunger;
            Thirst = thirst;
        }
    }

    private sealed class OriginalSettings
    {
        public readonly ItemInstance_Consumeable Consumeable;
        public readonly ItemInstance_Usable Usable;

        public OriginalSettings(ItemInstance_Consumeable consumeable, ItemInstance_Usable usable)
        {
            Consumeable = consumeable;
            Usable = usable;
        }
    }

    [HarmonyPatch(typeof(UseItemController), "Use")]
    private static class FlowerUsePatch
    {
        [HarmonyPrefix]
        private static void Prefix(UseItemController __instance, bool ___canUse, out Item_Base __state)
        {
            __state = null;
            if (!___canUse || __instance == null) return;

            Item_Base held = __instance.GetCurrentItemInHand();
            if (held != null && IsFlowerName(held.UniqueName))
                __state = held;
        }

        [HarmonyPostfix]
        private static void Postfix(UseItemController __instance, Item_Base __state)
        {
            if (__state == null) return;

            Network_Player player = RAPI.GetLocalPlayer();
            if (player == null || !player.IsLocalPlayer || player.Stats == null ||
                FindItemController(player) != __instance)
                return;

            // Характеристики пополняются только после принятого игрового использования
            player.Stats.Consume(__state);
        }
    }

    [HarmonyPatch(typeof(ItemManager), "LoadAllItems")]
    private static class ItemManagerLoadPatch
    {
        [HarmonyPostfix]
        private static void Postfix()
        {
            ApplyFlowerSettings();
        }
    }
}
