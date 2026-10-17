using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.ExceptionServices;
using Fungus;
using Mortal.Story;
using UnityEngine;
using UnityEngine.UI;

namespace LOMSave2
{
    // These shortcuts reproduce only the native, already-cached branch. In
    // particular, probing must never advance an iterator that we hand back.
    public static class LomsReplayFastPath
    {
        private const BindingFlags InstanceFields = BindingFlags.Instance
            | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;
        private static readonly Dictionary<Type, ResourceShape> Shapes = new Dictionary<Type, ResourceShape>();
        private static readonly FieldInfo Characters = Field(typeof(CharacterPlaceholder), "_characters", typeof(Dictionary<string, Character>));
        private static readonly FieldInfo ViewController = Field(typeof(FlowchartPlaceholder), "_viewController", typeof(ViewFlowchartController));
        private static readonly FieldInfo ViewImage = Field(typeof(ViewFlowchartController), "_viewImage", typeof(StoryViewImage));
        private static readonly FieldInfo ViewSprites = Field(typeof(StoryViewImage), "_dicView", typeof(Dictionary<string, Sprite>));
        private static readonly FieldInfo ViewRenderer = Field(typeof(StoryViewImage), "_spriteRenderer", typeof(SpriteRenderer));
        private static readonly FieldInfo TransitionImage = Field(typeof(StoryTransitionBlack), "_image", typeof(Image));
        private static readonly MethodInfo SetFillOrigin = typeof(StoryTransitionBlack).GetMethod("SetFillOrigin",
            InstanceFields, null, new[] { typeof(string) }, null);
        private static MethodInfo _completeImageTweens;

        public static int CacheHits { get; private set; }
        public static int FastTransitions { get; private set; }

        private enum ResourceKind { Character, PlaceholderView, ControllerView, ViewAsset }

        private sealed class ResourceShape
        {
            public ResourceKind Kind;
            public FieldInfo State;
            public FieldInfo Owner;
            public FieldInfo Name;
        }

        public static void ResetCounters()
        {
            CacheHits = 0;
            FastTransitions = 0;
        }

        private static bool Enabled => LomsRuntime.Replaying && LomsRuntime.InStoryScene
            && LomsRuntime.PausePanel == null && !LomsReplayGuard.RecoveryRequired;

        public static bool TryCompleteCachedResource(IEnumerator coroutine)
        {
            if (!Enabled || coroutine == null) return false;
            Type type = coroutine.GetType();
            ResourceShape shape;
            if (!Shapes.TryGetValue(type, out shape))
            {
                shape = Describe(type);
                Shapes.Add(type, shape);
            }
            if (shape == null || (int)shape.State.GetValue(coroutine) != 0) return false;
            var owner = shape.Owner.GetValue(coroutine) as UnityEngine.Object;
            string name = shape.Name.GetValue(coroutine) as string;
            if (owner == null || name == null) return false;

            if (shape.Kind == ResourceKind.Character)
            {
                if (Characters == null) return false;
                var characters = Characters.GetValue(owner) as Dictionary<string, Character>;
                Character character;
                if (characters == null || !characters.TryGetValue(name, out character) || character == null) return false;
            }
            else
            {
                StoryViewImage image;
                if (shape.Kind == ResourceKind.ViewAsset)
                {
                    image = owner as StoryViewImage;
                }
                else
                {
                    if (ViewController == null || ViewImage == null) return false;
                    var controller = shape.Kind == ResourceKind.PlaceholderView
                        ? ViewController.GetValue(owner) as ViewFlowchartController
                        : owner as ViewFlowchartController;
                    if (controller == null) return false;
                    // Native LoadView has no resource work for these colors.
                    if (name == "white" || name == "red")
                    {
                        CacheHits++;
                        return true;
                    }
                    // Leave black to native code: its special-case handling
                    // differs across inspected builds; never replace its sprite.
                    if (name == "black") return false;
                    image = ViewImage.GetValue(controller) as StoryViewImage;
                }
                if (image == null || ViewSprites == null || ViewRenderer == null) return false;
                var sprites = ViewSprites.GetValue(image) as Dictionary<string, Sprite>;
                Sprite sprite;
                if (sprites == null || !sprites.TryGetValue(name, out sprite) || sprite == null) return false;
                var renderer = ViewRenderer.GetValue(image) as SpriteRenderer;
                if (renderer == null) return false;
                // This assignment is the native cache branch's one side effect.
                // Let a real Unity setter exception reach the replay guard.
                renderer.sprite = sprite;
            }
            CacheHits++;
            return true;
        }

        private static ResourceShape Describe(Type type)
        {
            Type owner = type.DeclaringType;
            ResourceKind kind;
            string method;
            string name;
            if (owner == typeof(CharacterPlaceholder))
            {
                kind = ResourceKind.Character;
                method = "<LoadCharacterAsset>d__";
                name = "key";
            }
            else if (owner == typeof(FlowchartPlaceholder))
            {
                kind = ResourceKind.PlaceholderView;
                method = "<LoadView>d__";
                name = "name";
            }
            else if (owner == typeof(ViewFlowchartController))
            {
                kind = ResourceKind.ControllerView;
                method = "<LoadView>d__";
                name = "name";
            }
            else if (owner == typeof(StoryViewImage))
            {
                kind = ResourceKind.ViewAsset;
                method = "<LoadAsset>d__";
                name = "name";
            }
            else return null;
            if (!type.Name.StartsWith(method, StringComparison.Ordinal)) return null;
            FieldInfo state = Field(type, "<>1__state", typeof(int));
            FieldInfo instance = Field(type, "<>4__this", owner);
            FieldInfo argument = Field(type, name, typeof(string));
            if (state == null || instance == null || argument == null) return null;
            return new ResourceShape { Kind = kind, State = state, Owner = instance, Name = argument };
        }

        private static FieldInfo Field(Type type, string name, Type fieldType)
        {
            FieldInfo field = type.GetField(name, InstanceFields);
            return field != null && field.FieldType == fieldType ? field : null;
        }

        public static bool TryCompleteTransition(StoryTransitionBlack instance, bool entering, string dir)
        {
            if (!Enabled || instance == null || TransitionImage == null || SetFillOrigin == null
                || SetFillOrigin.ReturnType != typeof(void)) return false;
            var image = TransitionImage.GetValue(instance) as Image;
            StoryMainUI ui = StoryMainUI.Instance;
            StoryManager story = StoryManager.Instance;
            if (image == null || ui == null || story == null) return false;
            MethodInfo complete = FindCompleteImageTweens();
            if (complete == null) return false;

            // Only the black transition's own Image is a target. Complete its
            // previous tween before assigning the new endpoint, so a still-live
            // old fill tween cannot overwrite the completed transition later.
            // No global tween completion, time scale or async handle changes.
            Invoke(complete, null, new object[] { image, false });
            if (entering)
            {
                ui.Hide();
                story.SetHotKey(false);
            }
            Invoke(SetFillOrigin, instance, new object[] { dir });
            image.fillAmount = entering ? 1f : 0f;
            if (!entering)
            {
                ui.Show();
                story.SetHotKey(true);
            }
            FastTransitions++;
            return true;
        }

        private static MethodInfo FindCompleteImageTweens()
        {
            if (_completeImageTweens != null) return _completeImageTweens;
            // Keep DOTween optional for source builds that only reference the
            // game's public assemblies, and allow its assembly to load later.
            foreach (Assembly assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                Type type = assembly.GetType("DG.Tweening.DOTween", false);
                if (type == null) continue;
                MethodInfo complete = type.GetMethod("Complete", BindingFlags.Public | BindingFlags.Static,
                    null, new[] { typeof(object), typeof(bool) }, null);
                if (complete != null && complete.ReturnType == typeof(int)) _completeImageTweens = complete;
                return _completeImageTweens;
            }
            return null;
        }

        private static void Invoke(MethodInfo method, object instance, object[] arguments)
        {
            try { method.Invoke(instance, arguments); }
            catch (TargetInvocationException error)
            {
                if (error.InnerException != null) ExceptionDispatchInfo.Capture(error.InnerException).Throw();
                throw;
            }
        }
    }
}
