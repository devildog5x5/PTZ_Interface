using System.Windows;
using System.Windows.Input;

namespace PTZCameraOperator.Views
{
    /// <summary>
    /// Diagnostic Window for displaying camera diagnostic results
    /// </summary>
    public partial class DiagnosticWindow : Window
    {
        public DiagnosticWindow()
        {
            InitializeComponent();
        }

        public void AppendDiagnosticMessage(string message)
        {
            Dispatcher.Invoke(() =>
            {
                DiagnosticText.Text += message + "\n";
                DiagnosticText.ScrollToEnd();
            });
        }

        public void ClearDiagnostic()
        {
            Dispatcher.Invoke(() =>
            {
                DiagnosticText.Text = "";
            });
        }

        private void CopyResultsButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var results = DiagnosticText.Text;
                if (string.IsNullOrWhiteSpace(results))
                {
                    MessageBox.Show("No results to copy", "Empty", MessageBoxButton.OK, MessageBoxImage.Information);
                    return;
                }

                Clipboard.SetText(results);
                MessageBox.Show("✓ Results copied to clipboard!", "Copied", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (System.Exception ex)
            {
                MessageBox.Show($"Failed to copy: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void ClearButton_Click(object sender, RoutedEventArgs e)
        {
            DiagnosticText.Text = "";
        }
    }
}
