namespace Symlinker
{
    using System;
    using System.Windows;

    /// <summary>A small modal dialog that asks for one line of text.</summary>
    public partial class InputDialog : Window
    {
        private InputDialog(string message, string defaultText)
        {
            InitializeComponent();
            SourceInitialized += (sender, e) => TitleBarTheme.Apply(this);
            messageText.Text = message;
            inputBox.Text = defaultText ?? string.Empty;
            Loaded += (sender, e) =>
            {
                inputBox.Focus();
                inputBox.SelectAll();
            };
        }

        public string Result { get; private set; }

        /// <summary>Returns the entered text, or null if the dialog was cancelled.</summary>
        public static string Show(Window owner, string message, string defaultText)
        {
            var dialog = new InputDialog(message, defaultText) { Owner = owner };
            return dialog.ShowDialog() == true ? dialog.Result : null;
        }

        private void Ok_Click(object sender, RoutedEventArgs e)
        {
            Result = inputBox.Text;
            DialogResult = true;
        }
    }
}
