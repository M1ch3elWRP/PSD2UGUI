using System;
using System.Reflection;
using UnityEditor;
using UnityEngine;

namespace PSDImporter
{
    internal static class PSDEditorWindowUtility
    {
        public static T ShowCenteredUtility<T>(string title, Vector2 size, Vector2 minSize) where T : EditorWindow
        {
            T window = EditorWindow.GetWindow<T>(true, title, true);
            window.titleContent = new GUIContent(title);
            window.minSize = minSize;
            window.position = CenterRect(GetEditorMainWindowPosition(), size);
            window.ShowUtility();
            window.Focus();
            window.Repaint();
            return window;
        }

        private static Rect CenterRect(Rect host, Vector2 size)
        {
            if (host.width <= 0f || host.height <= 0f)
            {
                host = new Rect(0f, 0f, Mathf.Max(1280, Screen.currentResolution.width), Mathf.Max(720, Screen.currentResolution.height));
            }

            float width = Mathf.Min(size.x, Mathf.Max(size.x, host.width - 40f));
            float height = Mathf.Min(size.y, Mathf.Max(size.y, host.height - 80f));
            float x = host.x + Mathf.Max(20f, (host.width - width) * 0.5f);
            float y = host.y + Mathf.Max(40f, (host.height - height) * 0.5f);
            return new Rect(x, y, width, height);
        }

        private static Rect GetEditorMainWindowPosition()
        {
            try
            {
                Type containerWindowType = typeof(Editor).Assembly.GetType("UnityEditor.ContainerWindow");
                if (containerWindowType == null) return default;

                FieldInfo showModeField = containerWindowType.GetField("m_ShowMode", BindingFlags.NonPublic | BindingFlags.Instance);
                PropertyInfo positionProperty = containerWindowType.GetProperty("position", BindingFlags.Public | BindingFlags.Instance);
                if (showModeField == null || positionProperty == null) return default;

                UnityEngine.Object[] windows = Resources.FindObjectsOfTypeAll(containerWindowType);
                for (int i = 0; i < windows.Length; i++)
                {
                    object window = windows[i];
                    int showMode = Convert.ToInt32(showModeField.GetValue(window));
                    if (showMode == 4)
                    {
                        return (Rect)positionProperty.GetValue(window, null);
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[PSDTools] Failed to locate Unity main window, using screen center. {ex.Message}");
            }

            return default;
        }
    }
}
