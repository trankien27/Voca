using System.ComponentModel;
using System.IO;
using System.Windows;
using Voca.Services;

namespace Voca;

/// <summary>Picks plans and writes them to a .voca file or copies them as text for another computer.</summary>
public partial class ExportWindow : Window
{
    private readonly Store _store;
    private readonly List<Pick> _picks;

    private sealed class Pick : INotifyPropertyChanged
    {
        private bool _selected;
        public Guid Id { get; init; }
        public string Name { get; init; } = "";
        public string Meta { get; init; } = "";
        public bool InCourse { get; init; }
        public bool Selected
        {
            get => _selected;
            set { _selected = value; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Selected))); }
        }
        public event PropertyChangedEventHandler? PropertyChanged;
    }

    public ExportWindow(Store store)
    {
        InitializeComponent();
        _store = store;
        var data = store.Data;
        var ordered = CourseEngine.CoursePlans(data).Concat(data.Plans.Where(p => !data.Course.Contains(p.Id))).ToList();
        _picks = ordered.Select(p =>
        {
            var inCourse = data.Course.Contains(p.Id);
            var studied = p.Words.Count(w => w.Review is not null);
            return new Pick
            {
                Id = p.Id, Name = p.Name, InCourse = inCourse, Selected = inCourse,
                Meta = $"{(inCourse ? $"khóa học #{data.Course.IndexOf(p.Id) + 1}" : "ngoài khóa học")} · {p.DayCount} ngày · {p.Words.Count} từ · đã học {studied}"
            };
        }).ToList();
        PlansList.ItemsSource = _picks;
        UpdateStatus();
    }

    private IReadOnlyCollection<Guid> Selected => _picks.Where(p => p.Selected).Select(p => p.Id).ToList();

    private void UpdateStatus()
    {
        var plans = _store.Data.Plans.Where(p => Selected.Contains(p.Id)).ToList();
        StatusText.Text = plans.Count == 0 ? "Chưa chọn lộ trình nào." : $"Đã chọn {plans.Count} lộ trình, {plans.Sum(p => p.Words.Count)} từ.";
    }

    private void Selection_Changed(object sender, RoutedEventArgs e) => UpdateStatus();
    private void SelectAll_Click(object sender, RoutedEventArgs e) { foreach (var p in _picks) p.Selected = true; UpdateStatus(); }
    private void SelectCourse_Click(object sender, RoutedEventArgs e) { foreach (var p in _picks) p.Selected = p.InCourse; UpdateStatus(); }
    private void SelectNone_Click(object sender, RoutedEventArgs e) { foreach (var p in _picks) p.Selected = false; UpdateStatus(); }

    private void SaveFile_Click(object sender, RoutedEventArgs e)
    {
        if (Selected.Count == 0) { StatusText.Text = "Hãy chọn ít nhất một lộ trình."; return; }
        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            FileName = $"voca-{DateTime.Now:yyyyMMdd}{Transfer.Extension}",
            Filter = $"Gói Voca (*{Transfer.Extension})|*{Transfer.Extension}"
        };
        if (dialog.ShowDialog(this) != true) return;
        var withProgress = IncludeProgressBox.IsChecked == true;
        var package = Transfer.CreatePackage(_store.Data, Selected, withProgress);
        Transfer.Save(package, dialog.FileName);
        StatusText.Text = $"Đã lưu {package.Plans.Count} lộ trình ({package.Plans.Sum(p => p.Words.Count)} từ{(withProgress ? ", kèm tiến độ" : "")}) vào {Path.GetFileName(dialog.FileName)}. " +
                          "Chép file này sang máy kia rồi mở bằng Thư viện → Cài đặt → Nhập từ máy khác.";
    }

    private void CopyText_Click(object sender, RoutedEventArgs e)
    {
        if (Selected.Count == 0) { StatusText.Text = "Hãy chọn ít nhất một lộ trình."; return; }
        var text = Transfer.ToText(_store.Data, Selected);
        try
        {
            System.Windows.Clipboard.SetText(text);
            StatusText.Text = $"Đã sao chép {Selected.Count} lộ trình dạng văn bản ({text.Length:N0} ký tự). Trên máy kia: Thư viện → Tạo chủ đề mới → dán vào ô bước 3 → Xem trước → Nhập. (Văn bản không kèm tiến độ học.)";
        }
        catch (Exception)
        {
            StatusText.Text = "Không sao chép được (clipboard đang bận), hãy thử lại.";
        }
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
