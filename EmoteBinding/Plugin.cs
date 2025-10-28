using BepInEx;
using BepInEx.Configuration;
using HarmonyLib;
using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;
using PEAKEmoteLib;

namespace EmoteBinding
{
    [BepInPlugin("com.atomic.emotebinding", "Emote Binding", "1.0.1")]
    [BepInDependency("com.github.WaporVave.PEAKEmoteLib")]
    public class Plugin : BaseUnityPlugin
    {
        public static Plugin instance;
        public static Dictionary<string, ConfigEntry<KeyCode>> EmoteBindings = new();
        public static Dictionary<string, EmoteWheelData> EmoteData = new();

        private static Type emoteRegistryType;
        private static MethodInfo getEmotesMethod;

        private bool customEmotesRegistered = false;

        private static readonly MethodInfo playEmoteMethod = AccessTools.Method(typeof(CharacterAnimations), "PlayEmote");

        private void Awake()
        {
            instance = this;

            SetupEmoteRegistryReflection();
            RegisterDefaultEmotes();
            RegisterCustomEmotes();

            var harmony = new Harmony("com.atomic.emotebinding");
            harmony.PatchAll();
        }

        private void SetupEmoteRegistryReflection()
        {
            try
            {
                Assembly peakEmoteLibAssembly = Assembly.GetAssembly(typeof(PEAKEmoteLib.Emote));
                if (peakEmoteLibAssembly == null)
                {
                    Logger.LogError("PEAKEmoteLib assembly could not be found.");
                    return;
                }

                emoteRegistryType = peakEmoteLibAssembly.GetType("PEAKEmoteLib.EmoteRegistry");
                if (emoteRegistryType == null)
                {
                    Logger.LogError("PEAKEmoteLib.EmoteRegistry type not found via reflection.");
                    return;
                }

                getEmotesMethod = AccessTools.Method(emoteRegistryType, "GetEmotes");
                if (getEmotesMethod == null)
                {
                    Logger.LogError("PEAKEmoteLib.EmoteRegistry.GetEmotes() method not found via reflection.");
                }
            }
            catch (Exception ex)
            {
                Logger.LogError($"Error setting up EmoteRegistry reflection: {ex.Message}");
            }
        }

        private void Update()
        {
            if (!customEmotesRegistered)
            {
                RegisterCustomEmotes();
            }

            if (Character.localCharacter == null || Character.localCharacter.refs?.animations == null)
                return;

            foreach (var binding in EmoteBindings)
            {
                if (binding.Value.Value == KeyCode.None)
                    continue;

                if (Input.GetKeyDown(binding.Value.Value))
                {
                    if (EmoteData.TryGetValue(binding.Key, out var emoteData))
                    {
                        Logger.LogInfo($"Playing emote: {emoteData.emoteName} ({emoteData.anim})");
                        TryPlayEmote(emoteData.anim);
                    }
                    else
                    {
                        Logger.LogWarning($"No EmoteData found for {binding.Key}");
                    }
                }
            }
        }

        public static void TryPlayEmote(string emoteName)
        {
            if (Character.localCharacter?.refs?.animations == null)
                return;

            var anims = Character.localCharacter.refs.animations;
            if (playEmoteMethod == null)
            {
                instance.Logger.LogError("Could not find PlayEmote method!");
                return;
            }

            playEmoteMethod.Invoke(anims, new object[] { emoteName });
        }

        private void RegisterEmoteBinding(string section, KeyCode defaultKeycode, string emoteName, string animName)
        {
            // 1. Register/Bind Config
            var entry = Config.Bind(
                section,
                emoteName,
                defaultKeycode,
                $"Key binding for emote: {emoteName}"
            );

            EmoteBindings[emoteName] = entry;

            var emoteData = ScriptableObject.CreateInstance<EmoteWheelData>();
            emoteData.emoteName = emoteName;
            emoteData.anim = animName;

            EmoteData[emoteName] = emoteData;
        }

        private void RegisterDefaultEmotes()
        {
            const string section = "Default Emotes";

            RegisterEmoteBinding(section, KeyCode.Alpha5, "Thumbs Up", "A_Scout_Emote_ThumbsUp");
            RegisterEmoteBinding(section, KeyCode.Alpha6, "Think", "A_Scout_Emote_Think");
            RegisterEmoteBinding(section, KeyCode.Alpha7, "No-No", "A_Scout_Emote_Nono");
            RegisterEmoteBinding(section, KeyCode.Alpha8, "Play Dead", "A_Scout_Emote_Flex");
            RegisterEmoteBinding(section, KeyCode.Alpha9, "Shrug", "A_Scout_Emote_Shrug");
            RegisterEmoteBinding(section, KeyCode.Alpha0, "Crossed Arms", "A_Scout_Emote_CrossedArms");
            RegisterEmoteBinding(section, KeyCode.Minus, "Dance", "A_Scout_Emote_Dance1");
            RegisterEmoteBinding(section, KeyCode.Equals, "Salute", "A_Scout_Emote_Salute");
        }

        private void RegisterCustomEmotes()
        {
            if (getEmotesMethod == null)
            {
                return;
            }

            const string section = "Custom Emotes";

            var customEmotes = getEmotesMethod.Invoke(null, null) as IReadOnlyDictionary<string, Emote>;

            if (customEmotes == null)
            {
                Logger.LogError("Failed to invoke EmoteRegistry.GetEmotes() via reflection.");
                return;
            }

            int count = 0;

            foreach (var emotePair in customEmotes)
            {
                var emote = emotePair.Value;

                string bindingName = emote.Name;
                string animName = emote.Name;
                if (EmoteBindings.ContainsKey(bindingName))
                    continue;

                RegisterEmoteBinding(section, KeyCode.None, bindingName, animName);
                count++;
            }

            if (count > 0)
            {
                Logger.LogInfo($"Registered {count} new custom emote bindings from PEAKEmoteLib.");
            }

            if (customEmotes.Count > 0)
            {
                customEmotesRegistered = true;
            }
        }
    }
}
