using BepInEx;
using BepInEx.Configuration;
using BepInEx.Bootstrap;
using HarmonyLib;
using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;
using PEAKEmoteLib;

namespace EmoteBinding
{
    [BepInPlugin("com.atomic.emotebinding", "Emote Binding", "1.1.0")]
    [BepInDependency("com.github.WaporVave.PEAKEmoteLib", BepInDependency.DependencyFlags.SoftDependency)]
    public class Plugin : BaseUnityPlugin
    {
        public static Plugin instance;

        public static Dictionary<string, ConfigEntry<KeyCode>> EmoteBindings = new();
        public static Dictionary<string, EmoteWheelData> EmoteData = new();

        private static Type emoteRegistryType;
        private static MethodInfo getEmotesMethod;

        private bool baseEmotesRegistered = false;
        private bool customEmotesRegistered = false;

        private static readonly MethodInfo playEmoteMethod = AccessTools.Method(typeof(CharacterAnimations), "PlayEmote");

        private static readonly KeyCode[] DefaultKeySequence = new KeyCode[]
        {
            KeyCode.Alpha5, KeyCode.Alpha6, KeyCode.Alpha7, KeyCode.Alpha8,
            KeyCode.Alpha9, KeyCode.Alpha0, KeyCode.Minus, KeyCode.Equals
        };

        private void Awake()
        {
            instance = this;

            if (Chainloader.PluginInfos.ContainsKey("com.github.WaporVave.PEAKEmoteLib"))
            {
                SetupEmoteRegistryReflection();
            }
            else
            {
                customEmotesRegistered = true;
            }

            var harmony = new Harmony("com.atomic.emotebinding");
            harmony.PatchAll(Assembly.GetExecutingAssembly());

            Logger.LogInfo("Plugin Awake. Waiting for GUIManager to provide Emote Wheel.");
        }

        private void SetupEmoteRegistryReflection()
        {
            try
            {
                Assembly peakEmoteLibAssembly = Assembly.GetAssembly(typeof(PEAKEmoteLib.Emote));
                if (peakEmoteLibAssembly == null) return;

                emoteRegistryType = peakEmoteLibAssembly.GetType("PEAKEmoteLib.EmoteRegistry");
                if (emoteRegistryType != null)
                {
                    getEmotesMethod = AccessTools.Method(emoteRegistryType, "GetEmotes");
                }
            }
            catch (Exception ex)
            {
                Logger.LogError($"Reflection error on EmoteRegistry: {ex.Message}");
            }
        }

        public void RegisterEmotesFromWheel(GameObject wheelObject)
        {
            if (baseEmotesRegistered || wheelObject == null) return;

            var wheel = wheelObject.GetComponent<EmoteWheel>();
            if (wheel == null || wheel.data == null)
            {
                RegisterDefaultEmotesFallback();
                return;
            }

            int nextDefaultKeyIndex = 0;
            foreach (var emoteItem in wheel.data)
            {
                if (emoteItem == null || string.IsNullOrEmpty(emoteItem.emoteName)) continue;

                if (!EmoteBindings.ContainsKey(emoteItem.emoteName))
                {
                    KeyCode defaultKey = (nextDefaultKeyIndex < DefaultKeySequence.Length)
                        ? DefaultKeySequence[nextDefaultKeyIndex++]
                        : KeyCode.None;

                    RegisterBaseEmoteBinding("Base Game Emotes", defaultKey, emoteItem.emoteName, emoteItem.anim);
                }
            }

            baseEmotesRegistered = true;
            Logger.LogInfo("Registered base emotes directly from GUIManager.");
        }

        private void RegisterCustomEmotes()
        {
            if (getEmotesMethod == null || customEmotesRegistered) return;

            var customEmotes = getEmotesMethod.Invoke(null, null) as IReadOnlyDictionary<string, Emote>;
            if (customEmotes == null) return;

            foreach (var emotePair in customEmotes)
            {
                var emote = emotePair.Value;
                if (emote == null || string.IsNullOrEmpty(emote.Name)) continue;
                if (EmoteBindings.ContainsKey(emote.Name)) continue;

                var entry = Config.Bind("Custom Emotes", emote.Name, KeyCode.None, $"Key binding for: {emote.Name}");
                EmoteBindings[emote.Name] = entry;

                var emoteData = ScriptableObject.CreateInstance<EmoteWheelData>();
                emoteData.emoteName = emote.Name;
                emoteData.anim = emote.Name;
                EmoteData[emote.Name] = emoteData;
            }

            customEmotesRegistered = true;
        }

        private void RegisterDefaultEmotesFallback()
        {
            if (baseEmotesRegistered) return;
            baseEmotesRegistered = true;
            Logger.LogWarning("Using fallback emote list.");
        }

        private void RegisterBaseEmoteBinding(string section, KeyCode defaultKeycode, string emoteName, string animName)
        {
            var entry = Config.Bind(section, emoteName, defaultKeycode, $"Key binding for: {emoteName}");
            EmoteBindings[emoteName] = entry;

            var emoteData = ScriptableObject.CreateInstance<EmoteWheelData>();
            emoteData.emoteName = emoteName;
            emoteData.anim = animName;
            EmoteData[emoteName] = emoteData;
        }

        private void Update()
        {
            if (!customEmotesRegistered) RegisterCustomEmotes();

            if (Character.localCharacter?.refs?.animations == null) return;

            foreach (var binding in EmoteBindings)
            {
                if (binding.Value.Value != KeyCode.None && Input.GetKeyDown(binding.Value.Value))
                {
                    if (EmoteData.TryGetValue(binding.Key, out var data))
                    {
                        TryPlayEmote(data.anim);
                    }
                }
            }
        }

        public static void TryPlayEmote(string animName)
        {
            var anims = Character.localCharacter?.refs?.animations;
            if (anims != null && playEmoteMethod != null)
            {
                playEmoteMethod.Invoke(anims, new object[] { animName });
            }
        }
    }

    [HarmonyPatch]
    public class Patches
    {
        [HarmonyPostfix]
        [HarmonyPatch(typeof(GUIManager), "Awake")]
        public static void RegEmoteWheel(GUIManager __instance)
        {
            Plugin.instance.RegisterEmotesFromWheel(__instance.emoteWheel);
        }
    }
}