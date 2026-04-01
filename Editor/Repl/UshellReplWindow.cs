using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace Ushell.Editor
{
    internal sealed class UshellReplWindow : EditorWindow
    {
        private static class Styles
        {
            public static readonly GUIStyle TextAreaStyle;

            private static readonly Color LightBackground = new Color(0.87f, 0.87f, 0.87f);
            private static readonly Color DarkBackground = new Color(0.2f, 0.2f, 0.2f);
            private static readonly Color LightText = new Color(0.0f, 0.0f, 0.0f);
            private static readonly Color DarkText = new Color(0.706f, 0.706f, 0.706f);

            private static Texture2D _backgroundTexture;

            private static Texture2D BackgroundTexture
            {
                get
                {
                    if (_backgroundTexture == null)
                    {
                        _backgroundTexture = new Texture2D(1, 1, TextureFormat.RGBA32, false, true);
                        _backgroundTexture.SetPixel(0, 0, EditorGUIUtility.isProSkin ? DarkBackground : LightBackground);
                        _backgroundTexture.Apply();
                    }

                    return _backgroundTexture;
                }
            }

            static Styles()
            {
                TextAreaStyle = new GUIStyle(EditorStyles.textArea);
                TextAreaStyle.padding = new RectOffset();

                GUIStyleState style = TextAreaStyle.focused;
                style.background = BackgroundTexture;
                style.textColor = EditorGUIUtility.isProSkin ? DarkText : LightText;

                TextAreaStyle.focused = style;
                TextAreaStyle.active = style;
                TextAreaStyle.onActive = style;
                TextAreaStyle.hover = style;
                TextAreaStyle.normal = style;
                TextAreaStyle.onNormal = style;
            }
        }

        private const string ConsoleTextAreaControlName = "UshellConsoleTextArea";
        private const string CommandName = "command > ";

        [SerializeField]
        private UshellAutocompleteBox _autocompleteBox;

        [SerializeField]
        private Vector2 _scrollPosition = Vector2.zero;

        [SerializeField]
        private TextEditor _textEditor;

        [SerializeField]
        private List<string> _inputHistory = new List<string>();

        [SerializeField]
        private bool _captureLogs;

        private int _positionInHistory;
        private bool _requestMoveCursorToEnd;
        private bool _requestFocusOnTextArea;
        private bool _requestRevertNewLine;
        private string _input = string.Empty;
        private string _lastWord = string.Empty;
        private string _savedInput;
        private Vector2 _lastCursorPosition;

        private string ConsoleText
        {
            get => _textEditor.text;
            set => _textEditor.text = value;
        }

        [MenuItem("Tools/Ushell/Open REPL")]
        private static void OpenWindow()
        {
            GetWindow<UshellReplWindow>("Ushell REPL");
        }

        private void Awake()
        {
            ClearText();
            _requestFocusOnTextArea = true;
            if (_autocompleteBox == null)
            {
                _autocompleteBox = new UshellAutocompleteBox();
            }

            _captureLogs = UshellSettings.Instance.EnableVerboseLogs;
            UshellRoslynExecutor.WarmUp();
        }

        private void OnEnable()
        {
            if (_autocompleteBox == null)
            {
                _autocompleteBox = new UshellAutocompleteBox();
            }

            _autocompleteBox.onConfirm -= OnAutocompleteConfirm;
            _autocompleteBox.onConfirm += OnAutocompleteConfirm;
            _autocompleteBox.Clear();
            ScheduleMoveCursorToEnd();
            UshellRoslynExecutor.WarmUp();
        }

        private void OnDisable()
        {
            if (_autocompleteBox != null)
            {
                _autocompleteBox.onConfirm -= OnAutocompleteConfirm;
            }
        }

        private void OnInspectorUpdate()
        {
            Repaint();
        }

        private void OnGUI()
        {
            _textEditor = (TextEditor)GUIUtility.GetStateObject(typeof(TextEditor), GUIUtility.keyboardControl);
            if (ConsoleText == string.Empty)
            {
                AppendStartCommand();
                ScheduleMoveCursorToEnd();
            }

            HandleInvalidTypePositions();
            _autocompleteBox.HandleEvents();
            HandleHistory();
            DoAutoComplete();
            HandleRequests();
            DrawAll();
        }

        private void ClearText()
        {
            if (_textEditor != null)
            {
                ConsoleText = string.Empty;
            }
        }

        private void OnAutocompleteConfirm(string confirmedInput)
        {
            ConsoleText = ConsoleText.Substring(0, ConsoleText.Length - _lastWord.Length);
            ConsoleText += confirmedInput;
            _lastWord = confirmedInput;
            _requestRevertNewLine = true;
        }

        private void HandleHistory()
        {
            Event current = Event.current;
            if (current.type != EventType.KeyDown)
            {
                return;
            }

            bool changed = false;
            if (current.keyCode == KeyCode.DownArrow)
            {
                _positionInHistory++;
                changed = true;
                current.Use();
            }

            if (current.keyCode == KeyCode.UpArrow)
            {
                _positionInHistory--;
                changed = true;
                current.Use();
            }

            if (!changed)
            {
                return;
            }

            if (_savedInput == null)
            {
                _savedInput = _input;
            }

            if (_positionInHistory < 0)
            {
                _positionInHistory = 0;
            }
            else if (_positionInHistory >= _inputHistory.Count)
            {
                ReplaceCurrentCommand(_savedInput);
                _positionInHistory = _inputHistory.Count;
                _savedInput = null;
            }
            else
            {
                ReplaceCurrentCommand(_inputHistory[_positionInHistory]);
            }
        }

        private void ReplaceCurrentCommand(string replacement)
        {
            ConsoleText = ConsoleText.Substring(0, ConsoleText.Length - _input.Length);
            ConsoleText += replacement;
            _textEditor.MoveTextEnd();
        }

        private void DoAutoComplete()
        {
            string newInput = GetInput();
            if (newInput == null || string.Equals(_input, newInput, StringComparison.Ordinal) || _requestRevertNewLine)
            {
                return;
            }

            _input = newInput;
            _lastWord = _input;
            int lastWordIndex = _input.LastIndexOfAny(new[] { '(', ' ' });
            if (lastWordIndex != -1)
            {
                _lastWord = _input.Substring(lastWordIndex + 1);
            }

            UshellRoslynExecutor.RequestCompletions(_lastWord);
        }

        private string GetInput()
        {
            int commandStartIndex = ConsoleText.LastIndexOf(CommandName, StringComparison.Ordinal);
            if (commandStartIndex == -1)
            {
                return null;
            }

            commandStartIndex += CommandName.Length;
            return ConsoleText.Substring(commandStartIndex);
        }

        private void HandleRequests()
        {
            Event current = Event.current;
            if (_requestMoveCursorToEnd && current.type == EventType.Repaint)
            {
                _textEditor.MoveTextEnd();
                _requestMoveCursorToEnd = false;
                Repaint();
            }
            else if (focusedWindow == this && _requestFocusOnTextArea)
            {
                GUI.FocusControl(ConsoleTextAreaControlName);
                _requestFocusOnTextArea = false;
                Repaint();
            }

            Vector2 cursorPosition = _textEditor.graphicalCursorPos;
            if (current.type == EventType.Repaint && cursorPosition.y > _lastCursorPosition.y && _requestRevertNewLine)
            {
                _textEditor.Backspace();
                _textEditor.MoveTextEnd();
                Repaint();
                _requestRevertNewLine = false;
            }

            _lastCursorPosition = cursorPosition;
        }

        private void HandleInvalidTypePositions()
        {
            Event current = Event.current;
            if (!current.isKey || current.command || current.control)
            {
                return;
            }

            int lastIndexCommand = ConsoleText.LastIndexOf(CommandName, StringComparison.Ordinal) + CommandName.Length;
            int cursorIndex = _textEditor.cursorIndex;
            if (current.keyCode == KeyCode.Backspace)
            {
                cursorIndex--;
            }

            if (cursorIndex < lastIndexCommand)
            {
                ScheduleMoveCursorToEnd();
                current.Use();
            }
        }

        private void DrawAll()
        {
            GUI.DrawTexture(new Rect(0, 0, maxSize.x, maxSize.y), EditorGUIUtility.whiteTexture, ScaleMode.StretchToFill, false, 0f, EditorGUIUtility.isProSkin ? new Color(0.2f, 0.2f, 0.2f) : new Color(0.87f, 0.87f, 0.87f), 0f, 0f);

            EditorGUILayout.BeginHorizontal(EditorStyles.toolbar);
            _captureLogs = GUILayout.Toggle(_captureLogs, "Capture Logs", EditorStyles.toolbarButton, GUILayout.Width(92f));
            GUILayout.FlexibleSpace();

            if (GUILayout.Button("Clear", EditorStyles.toolbarButton))
            {
                ClearText();
            }

            EditorGUILayout.EndHorizontal();

            _scrollPosition = EditorGUILayout.BeginScrollView(_scrollPosition);
            DrawConsole();
            EditorGUILayout.EndScrollView();

            _autocompleteBox.results = UshellRoslynExecutor.GetCompletionSnapshot();

            Vector2 position = _textEditor.graphicalCursorPos;
            Rect rect = new Rect(position.x, position.y + 34f, 300f, 200f);
            _autocompleteBox.OnGUI(_lastWord, rect);
        }

        private void DrawConsole()
        {
            Event current = Event.current;
            if (current.type == EventType.KeyDown)
            {
                ScrollDown();
                if (current.keyCode == KeyCode.Return && !current.shift)
                {
                    _textEditor.MoveTextEnd();
                    ExecuteCurrentCommand();
                    current.Use();
                }
            }

            GUI.SetNextControlName(ConsoleTextAreaControlName);
            GUILayout.TextArea(ConsoleText, Styles.TextAreaStyle, GUILayout.ExpandHeight(true), GUILayout.ExpandWidth(true));
        }

        private void ExecuteCurrentCommand()
        {
            string command = _input?.Trim();
            if (string.IsNullOrWhiteSpace(command))
            {
                Append("\n");
                AppendStartCommand();
                ScheduleMoveCursorToEnd();
                return;
            }

            try
            {
                UshellCodeExecutionResult result = UshellRoslynExecutor.Execute(command, _captureLogs, UshellSettings.Instance.MaxExecutionSeconds * 1000);
                AppendExecutionResult(result);
                _inputHistory.Add(command);
                _positionInHistory = _inputHistory.Count;
                _savedInput = null;
            }
            catch (Exception exception)
            {
                Debug.LogException(exception);
                Append($"\n{exception.Message}\n");
            }

            _input = string.Empty;
            _lastWord = string.Empty;
            _autocompleteBox.Clear();
            AppendStartCommand();
            ScheduleMoveCursorToEnd();
        }

        private void AppendExecutionResult(UshellCodeExecutionResult result)
        {
            if (result == null)
            {
                Append("\nExecution returned no result.\n");
                return;
            }

            string summary;
            if (result.Success)
            {
                summary = result.ReturnValue == null ? "Executed code successfully" : FormatValue(result.ReturnValue);
            }
            else
            {
                summary = string.IsNullOrWhiteSpace(result.ErrorCode)
                    ? result.ErrorMessage
                    : $"{result.ErrorCode}: {result.ErrorMessage}";
            }

            Append($"\n{summary}\n");

            if (!result.Success && result.Details != null)
            {
                Append($"{FormatValue(result.Details)}\n");
            }

            foreach (string warning in result.Warnings)
            {
                Append($"Warning: {warning}\n");
            }

            foreach (Dictionary<string, object> log in result.Logs)
            {
                string type = log != null && log.TryGetValue("type", out object typeValue) ? typeValue?.ToString() ?? "Log" : "Log";
                string message = log != null && log.TryGetValue("message", out object messageValue) ? messageValue?.ToString() ?? string.Empty : string.Empty;
                Append($"{type}: {message}\n");
            }

            Append($"{result.DurationMs} ms\n");
        }

        private static string FormatValue(object value)
        {
            if (value == null)
            {
                return "null";
            }

            if (value is string stringValue)
            {
                return stringValue;
            }

            try
            {
                return MiniJson.Serialize(value);
            }
            catch
            {
                return value.ToString();
            }
        }

        private void ScrollDown()
        {
            _scrollPosition.y = float.MaxValue;
        }

        private void AppendStartCommand()
        {
            Append(CommandName);
        }

        private void ScheduleMoveCursorToEnd()
        {
            _requestMoveCursorToEnd = true;
            ScrollDown();
        }

        private void Append(string text)
        {
            ConsoleText += text;
        }
    }
}
