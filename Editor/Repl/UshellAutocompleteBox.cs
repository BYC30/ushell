using System;
using UnityEditor;
using UnityEngine;

namespace Ushell.Editor
{
    [Serializable]
    internal sealed class UshellAutocompleteBox
    {
        private static class Styles
        {
            public const float ResultHeight = 20f;
            public const float ResultsBorderWidth = 2f;
            public const float ResultsMargin = 15f;
            public const float ResultsLabelOffset = 2f;

            public static readonly GUIStyle EntryEven;
            public static readonly GUIStyle EntryOdd;
            public static readonly GUIStyle LabelStyle;
            public static readonly GUIStyle ResultsBorderStyle;
            public static readonly GUIStyle SliderStyle;

            static Styles()
            {
                EntryOdd = new GUIStyle("CN EntryBackOdd");
                EntryEven = new GUIStyle("CN EntryBackEven");
                ResultsBorderStyle = new GUIStyle("hostview");
                LabelStyle = new GUIStyle(EditorStyles.label)
                {
                    alignment = TextAnchor.MiddleLeft,
                    richText = true
                };
                SliderStyle = new GUIStyle("MiniSliderVertical");
            }
        }

        private const int HintToNextCompletionHeight = 7;

        public Action<string> onConfirm;
        public int maxResults = 10;

        [SerializeField]
        public string[] results = Array.Empty<string>();

        [SerializeField]
        private int _selectedIndex = -1;

        [SerializeField]
        private int _visualIndex = -1;

        private bool _showResults;
        private string _searchString;

        public void Clear()
        {
            _searchString = string.Empty;
            _showResults = false;
            results = Array.Empty<string>();
            _selectedIndex = -1;
            _visualIndex = -1;
        }

        public void OnGUI(string result, Rect rect)
        {
            if (results == null)
            {
                results = Array.Empty<string>();
            }

            if (!string.Equals(result, _searchString, StringComparison.Ordinal))
            {
                _selectedIndex = 0;
                _visualIndex = 0;
                _showResults = true;
            }

            _searchString = result;
            DrawResults(rect);
        }

        public void HandleEvents()
        {
            if (results == null || results.Length == 0)
            {
                return;
            }

            Event current = Event.current;
            if (current.type != EventType.KeyDown)
            {
                return;
            }

            switch (current.keyCode)
            {
                case KeyCode.Escape:
                    _showResults = false;
                    break;
                case KeyCode.UpArrow:
                    current.Use();
                    _selectedIndex--;
                    break;
                case KeyCode.DownArrow:
                    current.Use();
                    _selectedIndex++;
                    break;
                case KeyCode.Return:
                    if (_selectedIndex >= 0)
                    {
                        current.Use();
                        Confirm(results[_selectedIndex]);
                    }

                    break;
            }

            if (_selectedIndex >= results.Length)
            {
                _selectedIndex = 0;
            }
            else if (_selectedIndex < 0)
            {
                _selectedIndex = results.Length - 1;
            }
        }

        private void DrawResults(Rect drawRect)
        {
            if (results == null || results.Length == 0 || !_showResults)
            {
                return;
            }

            Event current = Event.current;
            drawRect.height = Styles.ResultHeight * Mathf.Min(maxResults, results.Length);
            drawRect.x = Styles.ResultsMargin;
            drawRect.width -= Styles.ResultsMargin * 2f;
            drawRect.height += Styles.ResultsBorderWidth;

            Rect backgroundRect = drawRect;
            if (results.Length > maxResults)
            {
                backgroundRect.height += HintToNextCompletionHeight + Styles.ResultsBorderWidth;
            }

            GUI.color = new Color(0.78f, 0.78f, 0.78f);
            GUI.Label(backgroundRect, string.Empty, Styles.ResultsBorderStyle);
            GUI.color = Color.white;

            Rect elementRect = drawRect;
            elementRect.x += Styles.ResultsBorderWidth;
            elementRect.width -= Styles.ResultsBorderWidth * 2f;
            elementRect.height = Styles.ResultHeight;

            Rect clipRect = drawRect;
            clipRect.height += HintToNextCompletionHeight;
            UpdateVisualIndex(clipRect);

            GUI.BeginClip(clipRect);
            {
                elementRect.x = Styles.ResultsBorderWidth;
                elementRect.y = 0f;
                if (results.Length > maxResults)
                {
                    elementRect.y = -_visualIndex * Styles.ResultHeight;
                    float maxPosition = GetTotalResultsShown(clipRect) * Styles.ResultHeight - HintToNextCompletionHeight;
                    if (-elementRect.y > maxPosition)
                    {
                        elementRect.y = -maxPosition;
                    }
                }

                for (int index = 0; index < results.Length; index++)
                {
                    if (current.type == EventType.Repaint)
                    {
                        GUIStyle style = index % 2 == 0 ? Styles.EntryOdd : Styles.EntryEven;
                        style.Draw(elementRect, false, false, index == _selectedIndex, false);

                        Rect labelRect = elementRect;
                        labelRect.x += Styles.ResultsLabelOffset;
                        GUI.Label(labelRect, results[index], Styles.LabelStyle);
                    }

                    elementRect.y += Styles.ResultHeight;
                }

                if (results.Length > maxResults)
                {
                    DrawScroll(clipRect);
                }
            }
            GUI.EndClip();
        }

        private void DrawScroll(Rect clipRect)
        {
            Rect scrollRect = clipRect;
            scrollRect.x += scrollRect.width - 30f;
            scrollRect.y = 0f;

            int resultsShown = GetTotalResultsShown(clipRect);
            scrollRect.height = ((float)maxResults / resultsShown) * clipRect.height;
            scrollRect.y = ((float)_visualIndex / resultsShown) * (clipRect.height - scrollRect.height);

            GUI.Box(scrollRect, GUIContent.none, Styles.SliderStyle);
        }

        private int GetTotalResultsShown(Rect clipRect)
        {
            int resultsShown = results.Length;
            resultsShown -= (int)(clipRect.height / Styles.ResultHeight);
            return resultsShown;
        }

        private void UpdateVisualIndex(Rect clipRect)
        {
            float selectedPosition = _selectedIndex * Styles.ResultHeight;
            float visualPosition = _visualIndex * Styles.ResultHeight;
            float totalHeight = results.Length * Styles.ResultHeight;

            float max = clipRect.height;
            float min = Math.Min(0f, totalHeight - selectedPosition);
            float diffMax = selectedPosition - (visualPosition + max) + Styles.ResultHeight;
            float diffMin = (visualPosition + min) - selectedPosition;

            if (diffMax > 0f)
            {
                _visualIndex += Mathf.CeilToInt(diffMax / Styles.ResultHeight);
            }
            else if (diffMin > 0f)
            {
                _visualIndex -= Mathf.CeilToInt(diffMin / Styles.ResultHeight);
            }
        }

        private void Confirm(string result)
        {
            onConfirm?.Invoke(result);
            if (EditorWindow.focusedWindow != null)
            {
                EditorWindow.focusedWindow.Repaint();
            }

            _showResults = false;
            _searchString = result;
        }
    }
}
