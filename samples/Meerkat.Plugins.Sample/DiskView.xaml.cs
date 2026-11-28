using System.Windows.Controls;

namespace Meerkat.Plugins.Sample;

public partial class DiskView : UserControl
{
    public DiskView() => InitializeComponent();

    public void Show(string drive, double freeGigabytes, double totalGigabytes)
    {
        Drive.Text = $"DRIVE {drive}";
        Free.Text = $"{freeGigabytes:0.0} GB free";
        Used.Value = totalGigabytes > 0 ? 1 - (freeGigabytes / totalGigabytes) : 0;
        Total.Text = $"of {totalGigabytes:0} GB";
    }
}