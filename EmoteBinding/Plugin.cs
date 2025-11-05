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

            PatchEmoteWheel();

            var harmony = new Harmony("com.atomic.emotebinding");
            harmony.PatchAll(Assembly.GetExecutingAssembly());
        }

        #region Base Game Emotes (Do Not Change)
        private void PatchEmoteWheel()
        {
            var emoteWheelType = AccessTools.TypeByName("EmoteWheel");
            if (emoteWheelType != null)
            {
                var awake = AccessTools.Method(emoteWheelType, "Awake", new Type[0]);
                var start = AccessTools.Method(emoteWheelType, "Start", new Type[0]);
                var onEnable = AccessTools.Method(emoteWheelType, "OnEnable", new Type[0]);
                var targetMethod = awake ?? start ?? onEnable;

                if (targetMethod != null)
                {
                    var harmony = new Harmony("com.atomic.emotebinding");
                    harmony.Patch(targetMethod, prefix: new HarmonyMethod(typeof(Plugin), nameof(EmoteWheel_Init_Prefix)));
                    Logger.LogInfo($"Successfully patched {targetMethod.Name} for dynamic base emote discovery.");
                }
                else
                {
                    Logger.LogError("No Awake/Start/OnEnable found on EmoteWheel. Using fallback.");
                    RegisterDefaultEmotesFallback();
                }
            }
            else
            {
                Logger.LogError("Could not find type 'EmoteWheel'. Using fallback.");
                RegisterDefaultEmotesFallback();
            }
        }

        private static void EmoteWheel_Init_Prefix(object __instance)
        {
            if (instance == null || instance.baseEmotesRegistered) return;

            var dataField = __instance.GetType().GetField("data", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (dataField == null)
            {
                instance.Logger.LogError("EmoteWheel 'data' field not found.");
                instance.RegisterDefaultEmotesFallback();
                return;
            }

            var emoteDataList = dataField.GetValue(__instance) as System.Collections.IList;
            if (emoteDataList == null)
            {
                instance.Logger.LogError("EmoteWheel 'data' invalid or null.");
                instance.RegisterDefaultEmotesFallback();
                return;
            }

            int nextDefaultKeyIndex = 0;
            foreach (var emoteDataObject in emoteDataList)
            {
                if (emoteDataObject == null) continue;

                var emoteDataType = emoteDataObject.GetType();
                var emoteNameField = emoteDataType.GetField("emoteName", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                var animField = emoteDataType.GetField("anim", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);

                var emoteName = emoteNameField?.GetValue(emoteDataObject) as string;
                var animName = animField?.GetValue(emoteDataObject) as string;

                if (string.IsNullOrEmpty(emoteName) || string.IsNullOrEmpty(animName)) continue;

                if (!EmoteBindings.ContainsKey(emoteName))
                {
                    KeyCode defaultKey = KeyCode.None;
                    if (nextDefaultKeyIndex < DefaultKeySequence.Length)
                    {
                        defaultKey = DefaultKeySequence[nextDefaultKeyIndex];
                        nextDefaultKeyIndex++;
                    }
                    instance.RegisterBaseEmoteBinding("Base Game Emotes (Dynamic)", defaultKey, emoteName, animName);
                }
            }

            instance.baseEmotesRegistered = true;
        }

        private void RegisterDefaultEmotesFallback()
        {
            if (baseEmotesRegistered) return;

            const string section = "Default Emotes (Fallback)";
            RegisterBaseEmoteBinding(section, KeyCode.Alpha5, "Thumbs Up", "A_Scout_Emote_ThumbsUp");
            RegisterBaseEmoteBinding(section, KeyCode.Alpha6, "Think", "A_Scout_Emote_Think");
            RegisterBaseEmoteBinding(section, KeyCode.Alpha7, "No-No", "A_Scout_Emote_Nono");
            RegisterBaseEmoteBinding(section, KeyCode.Alpha8, "Play Dead", "A_Scout_Emote_Flex");
            RegisterBaseEmoteBinding(section, KeyCode.Alpha9, "Shrug", "A_Scout_Emote_Shrug");
            RegisterBaseEmoteBinding(section, KeyCode.Alpha0, "Crossed Arms", "A_Scout_Emote_CrossedArms");
            RegisterBaseEmoteBinding(section, KeyCode.Minus, "Dance", "A_Scout_Emote_Dance1");
            RegisterBaseEmoteBinding(section, KeyCode.Equals, "Salute", "A_Scout_Emote_Salute");

            baseEmotesRegistered = true;
            Logger.LogWarning("Dynamic discovery failed. Using hardcoded emote list.");
        }

        private void RegisterBaseEmoteBinding(string section, KeyCode defaultKeycode, string emoteName, string animName)
        {
            var entry = Config.Bind(section, emoteName, defaultKeycode, $"Key binding for emote: {emoteName}");
            EmoteBindings[emoteName] = entry;

            var emoteData = ScriptableObject.CreateInstance<EmoteWheelData>();
            emoteData.emoteName = emoteName;
            emoteData.anim = animName;

            EmoteData[emoteName] = emoteData;
        }
        #endregion

        #region Custom Emotes (Deferred Registration)
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

        private void RegisterCustomEmotes()
        {
            if (getEmotesMethod == null || customEmotesRegistered) return;

            const string section = "Custom Emotes";

            var customEmotes = getEmotesMethod.Invoke(null, null) as IReadOnlyDictionary<string, Emote>;
            if (customEmotes == null)
            {
                if (customEmotesRegistered)
                    Logger.LogError("Failed to invoke EmoteRegistry.GetEmotes() via reflection.");
                return;
            }

            int count = 0;
            foreach (var emotePair in customEmotes)
            {
                var emote = emotePair.Value;
                if (emote == null) continue;

                string bindingName = emote.Name;
                string animName = emote.Name;
                if (string.IsNullOrEmpty(bindingName)) continue;
                if (EmoteBindings.ContainsKey(bindingName)) continue;

                var entry = Config.Bind(section, bindingName, KeyCode.None, $"Key binding for emote: {bindingName}");
                EmoteBindings[bindingName] = entry;

                var emoteData = ScriptableObject.CreateInstance<EmoteWheelData>();
                emoteData.emoteName = bindingName;
                emoteData.anim = animName;
                EmoteData[bindingName] = emoteData;

                count++;
            }

            if (count > 0)
                Logger.LogInfo($"Registered {count} custom emotes into Config and EmoteWheel.");

            if (customEmotes.Count > 0)
            {
                customEmotesRegistered = true;
            }
        }
        #endregion

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
                if (binding.Value.Value == KeyCode.None) continue;

                if (Input.GetKeyDown(binding.Value.Value))
                {
                    if (EmoteData.TryGetValue(binding.Key, out var emoteData))
                    {
                        TryPlayEmote(emoteData.anim);
                    }
                }
            }
        }

        public static void TryPlayEmote(string emoteName)
        {
            if (Character.localCharacter?.refs?.animations == null) return;

            var anims = Character.localCharacter.refs.animations;
            if (playEmoteMethod == null)
            {
                instance.Logger.LogError("Could not find PlayEmote method!");
                return;
            }

            playEmoteMethod.Invoke(anims, new object[] { emoteName });
        }
    }
}