using System;
using System.Linq;
using AutonomousCompanion.Demo;
using TMPro;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem.UI;
using UnityEngine.SceneManagement;

namespace AutonomousCompanion.Editor
{
    public static class CoopCampaignSetup
    {
        public const string First = "Assets/Scenes/AutonomousCoopDemo.unity";
        public const string Second = "Assets/Scenes/CoopRelayLevel.unity";

        [MenuItem("Tools/Autonomous Companion/Connect Both Levels (Local Play)")]
        public static void ConnectBothLevels()
        {
            if (EditorApplication.isPlayingOrWillChangePlaymode) throw new InvalidOperationException("Stop Play Mode first.");
            for (int i=0; i<SceneManager.sceneCount; i++)
                if (SceneManager.GetSceneAt(i).isDirty) throw new InvalidOperationException("Save scene edits first.");
            foreach (string path in new[]{First, Second})
            {
                var scene = EditorSceneManager.OpenScene(path, OpenSceneMode.Single);
                ConfigureScene(scene, path == First);
                if (!EditorSceneManager.SaveScene(scene)) throw new InvalidOperationException("Unable to save " + path);
            }
            var others = EditorBuildSettings.scenes.Where(s=>s.path!=First && s.path!=Second);
            EditorBuildSettings.scenes = new[]{new EditorBuildSettingsScene(First,true),new EditorBuildSettingsScene(Second,true)}.Concat(others).ToArray();
            AssetDatabase.SaveAssets();
            EditorSceneManager.OpenScene(First, OpenSceneMode.Single);
        }

        public static void ConfigureScene(Scene scene, bool first)
        {
            var roots=scene.GetRootGameObjects();
            var level=roots.SelectMany(r=>r.GetComponentsInChildren<DemoLevel>(true)).Single();
            foreach (var brain in roots.SelectMany(r=>r.GetComponentsInChildren<AutonomousCompanionBrain>(true))) brain.enabled=false;
            foreach (var client in roots.SelectMany(r=>r.GetComponentsInChildren<DecisionHttpClient>(true))) client.enabled=false;
            foreach (var executor in roots.SelectMany(r=>r.GetComponentsInChildren<CompanionExecutor>(true))) executor.enabled=false;
            foreach (var planner in roots.SelectMany(r=>r.GetComponentsInChildren<LocalDecisionProvider>(true))) planner.enabled=false;
            level.player.allowKeyboard=false;
            level.companion.allowKeyboard=false;
            var controls=level.GetComponent<RelayManualControls>() ?? level.gameObject.AddComponent<RelayManualControls>();
            controls.level=level;
            controls.enabled=true;
            if (first)
            {
                foreach(var old in roots.SelectMany(r=>r.GetComponentsInChildren<DemoStatusHud>(true)))
                {
                    old.enabled=false;
                    var hud=old.GetComponent<RelayLevelHud>() ?? old.gameObject.AddComponent<RelayLevelHud>();
                    hud.level=level; hud.label=old.label; hud.firstGapBeforeLatch=false;
                }
                foreach(var text in roots.SelectMany(r=>r.GetComponentsInChildren<TMP_Text>(true)))
                {
                    if(text.text.StartsWith("AUTONOMOUS COMPANION")) text.text="LOCAL PLAY / COOPERATIVE PLATFORMING";
                    if(text.text.StartsWith("MOVE  A / D")) text.text="PLAYER  A / D + Space     PARTNER  J / L + I     RESET  R";
                    if(text.text=="AI") text.text="PARTNER";
                }
            }
            var flow=level.GetComponent<CoopLevelFlow>() ?? level.gameObject.AddComponent<CoopLevelFlow>();
            flow.level=level; flow.destinationScene=first?Second:First;
            flow.completionText=first?"LEVEL 01 COMPLETE":"BOTH LEVELS COMPLETE";
            if (flow.completionPanel == null) BuildPanel(flow, first);
            flow.completionPanel.SetActive(false);
            if (!roots.SelectMany(r=>r.GetComponentsInChildren<EventSystem>(true)).Any())
            {
                var events=new GameObject("EventSystem",typeof(EventSystem));
                var input=events.AddComponent<InputSystemUIInputModule>(); input.AssignDefaultActions();
            }
            EditorSceneManager.MarkSceneDirty(scene);
        }

        private static void BuildPanel(CoopLevelFlow flow, bool first)
        {
            var font=AssetDatabase.LoadAssetAtPath<TMP_FontAsset>("Assets/TextMesh Pro/Resources/Fonts & Materials/LiberationSans SDF.asset");
            var root=new GameObject("CompletionHUD",typeof(RectTransform),typeof(Canvas),typeof(UnityEngine.UI.CanvasScaler),typeof(UnityEngine.UI.GraphicRaycaster));
            var canvas=root.GetComponent<Canvas>(); canvas.renderMode=RenderMode.ScreenSpaceOverlay; canvas.sortingOrder=20;
            var scaler=root.GetComponent<UnityEngine.UI.CanvasScaler>(); scaler.uiScaleMode=UnityEngine.UI.CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution=new Vector2(1920,1080); scaler.screenMatchMode=UnityEngine.UI.CanvasScaler.ScreenMatchMode.Expand;
            var panel=new GameObject("CompletionPanel",typeof(RectTransform),typeof(UnityEngine.UI.Image)); panel.transform.SetParent(root.transform,false);
            var rect=panel.GetComponent<RectTransform>(); rect.anchorMin=rect.anchorMax=new Vector2(.5f,.72f); rect.sizeDelta=new Vector2(860,230);
            var background=panel.GetComponent<UnityEngine.UI.Image>(); background.color=new Color(.035f,.08f,.13f,.97f); background.raycastTarget=false;
            flow.completionPanel=panel;
            flow.message=Text(panel.transform,"Result",flow.completionText,font,new Vector2(0,66),new Vector2(800,55),34);
            var buttonGo=new GameObject("ContinueButton",typeof(RectTransform),typeof(UnityEngine.UI.Image),typeof(UnityEngine.UI.Button)); buttonGo.transform.SetParent(panel.transform,false);
            var br=buttonGo.GetComponent<RectTransform>(); br.sizeDelta=new Vector2(550,60); br.anchoredPosition=new Vector2(0,0);
            var image=buttonGo.GetComponent<UnityEngine.UI.Image>(); image.color=new Color(.22f,.72f,.77f);
            var button=buttonGo.GetComponent<UnityEngine.UI.Button>(); button.targetGraphic=image; flow.continueButton=button;
            var label=Text(buttonGo.transform,"Label",first?"NEXT LEVEL  /  ENTER":"PLAY FROM LEVEL 01  /  ENTER",font,Vector2.zero,new Vector2(530,55),26);
            label.color=new Color(.025f,.06f,.1f);
            Text(panel.transform,"Replay","R  /  REPLAY THIS LEVEL",font,new Vector2(0,-69),new Vector2(800,40),22);
        }
        private static TextMeshProUGUI Text(Transform parent,string name,string value,TMP_FontAsset font,Vector2 pos,Vector2 size,float fontSize)
        {
            var go=new GameObject(name,typeof(RectTransform)); go.transform.SetParent(parent,false);
            var rect=go.GetComponent<RectTransform>(); rect.sizeDelta=size; rect.anchoredPosition=pos;
            var text=go.AddComponent<TextMeshProUGUI>(); text.text=value; text.font=font; text.fontSize=fontSize;
            text.alignment=TextAlignmentOptions.Center; text.color=new Color(.9f,.97f,1); text.raycastTarget=false;
            return text;
        }
    }
}
