using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEngine;

public static class DevAutoLoad
{
    private const float ComponentsTimeout = 90f;
    private const float MainMenuDelay = 3f;
    private const int MarkerLifetimeMinutes = 10;

    private static bool started;

    public static void Start(MonoBehaviour owner, string modId)
    {
        if (owner == null || started || string.IsNullOrWhiteSpace(modId))
        {
            return;
        }

        bool hasMarker = ConsumeBuildMarker(modId);
        if (!hasMarker)
        {
            Debug.Log("[DevAutoLoad] Автозагрузка пропущена - нет свежего маркера Rebuild для " + modId);
            return;
        }

        started = true;
        Debug.Log("[DevAutoLoad] Автозагрузка активирована для " + modId);
        try
        {
            owner.StartCoroutine(LoadLatestWorld());
        }
        catch (Exception ex)
        {
            started = false;
            Debug.LogError("[DevAutoLoad] Ошибка запуска корутины: " + ex);
        }
    }

    private static string GetMarkerPath(string modId)
    {
        string gameDirectory = Path.GetDirectoryName(Application.dataPath);
        return Path.Combine(gameDirectory, "mods", modId + ".autoload.flag");
    }

    private static bool HasFreshBuildMarker(string modId)
    {
        if (string.IsNullOrWhiteSpace(modId))
        {
            return false;
        }

        try
        {
            string path = GetMarkerPath(modId);
            if (!File.Exists(path))
            {
                return false;
            }

            TimeSpan age = DateTime.UtcNow - File.GetLastWriteTimeUtc(path);
            return age >= TimeSpan.FromMinutes(-1) && age <= TimeSpan.FromMinutes(MarkerLifetimeMinutes);
        }
        catch (Exception ex)
        {
            Debug.LogWarning("[DevAutoLoad] Не удалось проверить маркер Rebuild: " + ex.Message);
            return false;
        }
    }

    private static bool ConsumeBuildMarker(string modId)
    {
        try
        {
            string path = GetMarkerPath(modId);
            if (!File.Exists(path))
            {
                return false;
            }

            bool fresh = HasFreshBuildMarker(modId);
            if (!fresh)
            {
                Debug.LogWarning("[DevAutoLoad] Маркер Rebuild устарел: " + path);
            }
            File.Delete(path);
            return fresh;
        }
        catch (Exception ex)
        {
            Debug.LogWarning("[DevAutoLoad] Не удалось удалить маркер Rebuild: " + ex.Message);
            return false;
        }
    }

    private static IEnumerator LoadLatestWorld()
    {
        Debug.Log("[DevAutoLoad] Ожидание компонентов SaveAndLoad и Raft_Network");
        float timeoutAt = Time.realtimeSinceStartup + ComponentsTimeout;

        while (ComponentManager<SaveAndLoad>.Value == null ||
               ComponentManager<Raft_Network>.Value == null)
        {
            if (Time.realtimeSinceStartup >= timeoutAt)
            {
                Debug.LogError("[DevAutoLoad] Компоненты загрузки мира не появились за 90 секунд");
                yield break;
            }

            yield return null;
        }

        if (LoadSceneManager.IsGameSceneLoaded)
        {
            Debug.Log("[DevAutoLoad] Автозагрузка отменена - игровая сцена уже открыта");
            yield break;
        }

        Debug.Log("[DevAutoLoad] Компоненты готовы, ожидание главного меню");
        yield return new WaitForSecondsRealtime(MainMenuDelay);

        if (LoadSceneManager.IsGameSceneLoaded)
        {
            Debug.Log("[DevAutoLoad] Автозагрузка отменена - мир уже запускается");
            yield break;
        }

        SaveAndLoad saveAndLoad = ComponentManager<SaveAndLoad>.Value;
        Raft_Network network = ComponentManager<Raft_Network>.Value;

        if (saveAndLoad == null || network == null)
        {
            Debug.LogError("[DevAutoLoad] Компоненты загрузки мира исчезли до запуска");
            yield break;
        }

        saveAndLoad.CreateNecessaryLoadDirectories();
        saveAndLoad.ConvertAllOldSavesToNewFormat();

        Debug.Log("[DevAutoLoad] Путь сохранений: " + SaveAndLoad.WorldPath);
        DirectoryInfo worldRoot = new DirectoryInfo(SaveAndLoad.WorldPath);

        if (!worldRoot.Exists)
        {
            Debug.LogError("[DevAutoLoad] Папка сохранений не найдена");
            yield break;
        }

        List<GameToFolderConnection> worlds = new List<GameToFolderConnection>();

        foreach (DirectoryInfo directory in worldRoot.GetDirectories())
        {
            if (directory.Name == SaveAndLoad.BackupFolderName)
            {
                continue;
            }

            DirectoryInfo gameDirectoryInfo;
            RGD_Game game = SaveAndLoad.GetLatestRGDGameFromBackupFolders(
                directory, out gameDirectoryInfo);

            if (game == null)
            {
                continue;
            }

            worlds.Add(new GameToFolderConnection
            {
                rgdGame = game,
                directoryInfo = directory,
                gameDirectoryInfo = gameDirectoryInfo
            });
        }

        Debug.Log("[DevAutoLoad] Найдено сохранений: " + worlds.Count);
        GameToFolderConnection latest = worlds
            .OrderByDescending(GetLastSaveTimeUtc)
            .FirstOrDefault();

        if (latest == null || latest.rgdGame == null)
        {
            Debug.LogError("[DevAutoLoad] Не найдено сохранений для автозагрузки");
            yield break;
        }

        SaveAndLoad.WorldToLoad = latest.rgdGame;
        GameModeValueManager.SelectCurrentGameMode(SaveAndLoad.WorldToLoad.mode);
        GameManager.IsInNewGame = false;
        GameManager.FriendlyFire = false;

        Debug.Log("[DevAutoLoad] Запускается последнее изменённое сохранение");
        network.HostGame(RequestJoinAuthSetting.ALLOW_NONE, string.Empty);
    }

    private static DateTime GetLastSaveTimeUtc(GameToFolderConnection world)
    {
        if (world == null)
        {
            return DateTime.MinValue;
        }

        if (world.gameDirectoryInfo != null)
        {
            return world.gameDirectoryInfo.LastWriteTimeUtc;
        }

        return world.directoryInfo != null
            ? world.directoryInfo.LastWriteTimeUtc
            : DateTime.MinValue;
    }
}
