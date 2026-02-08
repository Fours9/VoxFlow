using System.Windows;
using System.Windows.Controls;
using VoxFlow.Core;

namespace VoxFlow.Utils
{
    public static class TextInsertHelper
    {
        public static void InsertTextToActiveEditor(string insertText, EditorState state, TextBox draftBox, TextBox liveBox)
        {
            if (state == null || string.IsNullOrEmpty(insertText)) return;

            TextBox? targetTextBox = null;
            if (state.ActiveEditor == ActiveEditor.Draft)
            {
                targetTextBox = draftBox;
            }
            else if (state.ActiveEditor == ActiveEditor.Live)
            {
                targetTextBox = liveBox;
            }

            if (targetTextBox == null) return;

            bool hasCaret = targetTextBox.IsKeyboardFocusWithin;

            if (hasCaret)
            {
                // Вставити на CaretIndex
                int idx = targetTextBox.CaretIndex;
                targetTextBox.Text = targetTextBox.Text.Insert(idx, insertText);
                targetTextBox.CaretIndex = idx + insertText.Length;
            }
            else
            {
                // Замінити весь текст
                targetTextBox.Text = insertText;
                targetTextBox.CaretIndex = insertText.Length;
                // БЕЗ Focus() - не красти focus
            }

            // Оновити EditorState
            if (state.ActiveEditor == ActiveEditor.Draft)
            {
                state.SharedEditorText = targetTextBox.Text;
            }
            else if (state.ActiveEditor == ActiveEditor.Live)
            {
                if (state.LiveDisabled)
                {
                    state.LiveDetachedText = targetTextBox.Text;
                }
                else
                {
                    state.SharedEditorText = targetTextBox.Text;
                }
            }
        }
    }
}