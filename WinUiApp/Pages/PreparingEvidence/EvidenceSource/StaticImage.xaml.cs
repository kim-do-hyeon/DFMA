using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Navigation;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices.WindowsRuntime;
using Windows.Foundation;
using Windows.Foundation.Collections;
using Windows.Storage.Pickers;
using WinRT.Interop;

using WinUiApp.Pages.ArtifactsAnalysis;
using WinUiApp.Pages.CaseAnalysis;
using WinUiApp.Interop;

namespace WinUiApp.Pages.CaseAnalysis.EvidenceSource
{
    public sealed partial class StaticImage : Page
    {
        // 페이지 이동 후에도 값 유지용 static 상태 (첫 번째 TextBox만)
        private static string? _savedImagePath;

        // 동적으로 만드는 증거 칸 번호용
        private int _evidenceIndex = 1;

        public StaticImage()
        {
            this.InitializeComponent();
            LoadSavedState();
            HookTextChangedForState();
        }

        // 저장 상태 불러오기
        private void LoadSavedState()
        {
            if (_savedImagePath is null)
            {
                CaseFolderPathTextBox.Text = string.Empty;
            }
            else
            {
                CaseFolderPathTextBox.Text = _savedImagePath;
            }
        }

        // TextBox 변경 시 자동 저장
        private void HookTextChangedForState()
        {
            CaseFolderPathTextBox.TextChanged += (_, __) => SaveState();
        }

        private void SaveState()
        {
            _savedImagePath = CaseFolderPathTextBox.Text;
        }

        private void DeleteEvidenceButton_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button button)
            {
                if (button.Tag is Grid rowGrid)
                {
                    // 동적으로 추가된 줄 삭제
                    EvidenceStackPanel.Children.Remove(rowGrid);
                }
                else
                {
                    // 첫 번째(기본) 칸 삭제 버튼은 비활성화 상태이므로 실제로는 안 들어오지만
                    if (button.Parent is Grid firstRow)
                    {
                        EvidenceStackPanel.Children.Remove(firstRow);
                    }
                }
            }
        }

        // "증거 추가" 버튼 클릭
        private void AddEvidenceButton_Click(object sender, RoutedEventArgs e)
        {
            var rowGrid = new Grid();
            rowGrid.Margin = new Thickness(0, 8, 0, 0);

            rowGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            rowGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            rowGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var textBox = new TextBox
            {
                Name = $"CaseFolderPathTextBox_{_evidenceIndex}",
                IsReadOnly = true
            };
            Grid.SetColumn(textBox, 0);

            var browseButton = new Button
            {
                Content = "찾아보기...",
                Margin = new Thickness(8, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Center,
                Tag = textBox   // 이 버튼이 채울 TextBox를 Tag로 보관
            };
            browseButton.Click += BrowseFolderButton_Click;
            Grid.SetColumn(browseButton, 1);

            // 삭제 버튼
            var deleteButton = new Button
            {
                Content = "삭제",
                Margin = new Thickness(8, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Center,
                Tag = rowGrid    // 어떤 줄(Grid)을 삭제할지 Tag에 보관
            };
            deleteButton.Click += DeleteEvidenceButton_Click;
            Grid.SetColumn(deleteButton, 2);

            rowGrid.Children.Add(textBox);
            rowGrid.Children.Add(browseButton);
            rowGrid.Children.Add(deleteButton);

            EvidenceStackPanel.Children.Add(rowGrid);
            _evidenceIndex++;
        }

        // 이미지 파일 찾아보기 버튼
        private async void BrowseFolderButton_Click(object sender, RoutedEventArgs e)
        {
            var picker = new FileOpenPicker();
            var hwnd = WindowNative.GetWindowHandle(App.MainWindowInstance);
            InitializeWithWindow.Initialize(picker, hwnd);

            picker.FileTypeFilter.Add(".e01");
            picker.FileTypeFilter.Add(".001");
            picker.FileTypeFilter.Add(".dd");
            picker.FileTypeFilter.Add(".raw");
            picker.FileTypeFilter.Add(".img");

            var file = await picker.PickSingleFileAsync();
            if (file != null)
            {
                // 어떤 TextBox에 넣을지 결정
                TextBox targetTextBox = CaseFolderPathTextBox;

                if (sender is Button button && button.Tag is TextBox taggedTextBox)
                {
                    // 동적으로 만든 칸 또는 XAML에서 Tag로 묶어둔 칸
                    targetTextBox = taggedTextBox;
                }

                targetTextBox.Text = file.Path;

                // 상태 저장은 기존처럼 첫 번째 칸만
                if (ReferenceEquals(targetTextBox, CaseFolderPathTextBox))
                {
                    SaveState();
                }
            }
        }

        // EvidenceStackPanel 안의 모든 TextBox에서 경로 리스트 가져오기
        private List<string> GetAllImagePaths()
        {
            var result = new List<string>();

            foreach (var child in EvidenceStackPanel.Children)
            {
                if (child is Grid rowGrid)
                {
                    var tb = rowGrid.Children.OfType<TextBox>().FirstOrDefault();
                    if (tb != null)
                    {
                        var path = tb.Text?.Trim();
                        if (!string.IsNullOrEmpty(path))
                            result.Add(path);
                    }
                }
            }

            return result;
        }

        // "증거 프로세싱" 버튼
        private async void EvidenceProcess_Button_Click(object sender, RoutedEventArgs e)
        {
            // 1) 현재 UI에 있는 모든 디스크 이미지 경로 모으기
            var imagePaths = GetAllImagePaths();

            if (imagePaths.Count == 0)
            {
                var dialog = new ContentDialog
                {
                    XamlRoot = this.XamlRoot,
                    Title = "이미지 파일 누락",
                    Content = "추가된 디스크 이미지 파일이 없습니다. 최소 1개 이상 선택해주세요.",
                    CloseButtonText = "확인"
                };
                await dialog.ShowAsync();
                return;
            }

            // 2) 현재 케이스 루트 폴더 가져오기
            var caseRoot = CreateCasePage.CurrentCaseRoot;
            if (string.IsNullOrEmpty(caseRoot))
            {
                await new ContentDialog
                {
                    XamlRoot = this.XamlRoot,
                    Title = "케이스 정보 없음",
                    Content = "케이스 폴더 정보를 찾을 수 없습니다.\n먼저 케이스를 생성하거나 열어주세요.",
                    CloseButtonText = "확인"
                }.ShowAsync();
                return;
            }

            // 3) 케이스 DB 경로
            string dbPath = Path.Combine(caseRoot, "DFMA-Case.dfmadb");

            if (!File.Exists(dbPath))
            {
                await new ContentDialog
                {
                    XamlRoot = this.XamlRoot,
                    Title = "DB 없음",
                    Content = $"케이스 DB 파일을 찾을 수 없습니다.\n경로: {dbPath}",
                    CloseButtonText = "확인"
                }.ShowAsync();
                return;
            }

            // 4) SQLite 열기
            IntPtr db;
            int flags = NativeSqliteHelper.SQLITE_OPEN_READWRITE;
            int rc = NativeSqliteHelper.sqlite3_open_v2(dbPath, out db, flags, null);

            if (rc != NativeSqliteHelper.SQLITE_OK)
            {
                await new ContentDialog
                {
                    XamlRoot = this.XamlRoot,
                    Title = "DB 열기 실패",
                    Content = $"데이터베이스를 열 수 없습니다.\n경로: {dbPath}\nrc={rc}",
                    CloseButtonText = "확인"
                }.ShowAsync();
                return;
            }

            try
            {
                // 5) evidence_source 테이블 생성 (id, type, value)
                string createTableSql =
                    "CREATE TABLE IF NOT EXISTS evidence_source (" +
                    " id    INTEGER PRIMARY KEY AUTOINCREMENT," +
                    " type  TEXT NOT NULL," +
                    " value TEXT NOT NULL" +
                    ");";

                NativeSqliteHelper.ExecNonQuery(db, createTableSql);

                // 기존 StaticImage 타입 데이터는 삭제하고 현재 UI 기준으로 다시 저장
                string deleteSql = "DELETE FROM evidence_source WHERE type = 'StaticImage';";
                NativeSqliteHelper.ExecNonQuery(db, deleteSql);

                // 6) 각 이미지 경로를 ROW로 저장
                foreach (var path in imagePaths)
                {
                    string safePath = path.Replace("'", "''");
                    string insertSql =
                        "INSERT INTO evidence_source(type, value) " +
                        $"VALUES('StaticImage', '{safePath}');";

                    NativeSqliteHelper.ExecNonQuery(db, insertSql);
                }

                // DB 저장 후 증거 프로세싱 페이지로 이동
                if (App.MainWindowInstance is MainWindow window)
                {
                    window.RootFrameControl.Navigate(typeof(WinUiApp.Pages.CaseAnalysisPage), "ArtifactsProcess");
                }
            }
            catch (Exception ex)
            {
                await new ContentDialog
                {
                    XamlRoot = this.XamlRoot,
                    Title = "저장 오류",
                    Content = $"evidence_source 저장 중 오류가 발생했습니다.\n{ex.Message}",
                    CloseButtonText = "확인"
                }.ShowAsync();
            }
            finally
            {
                NativeSqliteHelper.sqlite3_close(db);
            }
        }
    }
}
