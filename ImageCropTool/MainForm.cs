using OpenCvSharp;
using OpenCvSharp.Extensions;
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Threading.Tasks;
using System.Windows.Forms;
using Newtonsoft.Json;

// 충돌 방지 별칭
using DPoint = System.Drawing.Point;

namespace ImageCropTool
{
    public partial class MainForm : Form
    {

        /* =========================================================
         *  Context Menu (Line Delete)
         * ========================================================= */
        private ContextMenuStrip lineContextMenu;
        private BaseLineInfo contextTargetLine = null;  // 우클릭 대상 라인
        private CropBoxInfo selectedCropBox = null;


        enum ListViewMode
        {
            LineList,
            CropList
        }

        private ListViewMode currentListMode = ListViewMode.LineList;
        private BaseLineInfo currentLineInView = null;

        /* =========================================================
         *  Line / Crop Info
         * ========================================================= */

        private const int DefaultCropSize = 512;
        private List<BaseLineInfo> baseLines = new List<BaseLineInfo>();   // 모든 기준선 목록
        private BaseLineInfo currentLine = null;      // 현재 그리고 있는 기준선 (아직 완성 안됨)
        private CropBoxInfo hoveredBox = null;          // hover된 크롭박스 (모든 기준선 통합)

        /* =========================================================
         *  Image
         * ========================================================= */
        private Bitmap viewBitmap;
        //private Bitmap originalBitmap;
        private Mat originalMat;

        private string imageColorInfoText = string.Empty;
        private string currentImagePath = null;

        /* =========================================================
         *  Loading Spinner
         * ========================================================= */
        private bool isImageLoading = false;
        private Timer loadingTimer;
        private float spinnerAngle = 0f;

        /* =========================================================
         *  Drag / Click State
         * ========================================================= */
        private const int HitRadius = 8;

        private enum ClickState { None, OnePoint }
        private ClickState clickState = ClickState.None;

        /* =========================================================
         *  Drag (Multi Baseline)
         * ========================================================= */
        private BaseLineInfo draggingLine = null;

        private enum DragTarget
        {
            None,
            StartPoint,
            EndPoint
        }

        private DragTarget dragTarget = DragTarget.None;

        /* =========================================================
         *  Mouse Position Display
         * ========================================================= */
        private PointF mouseOriginalPt;    // 표시할 이미지 좌표
        private DPoint mouseScreenPt;      // 텍스트를 그릴 화면 위치

        /* =========================================================
         *  Crop Anchor
         * ========================================================= */
        private CropAnchor cropAnchor = CropAnchor.Center;

        /* =========================================================
         *  View Transform (Zoom & Pan)
         * ========================================================= */
        private float viewScale = 1.0f;                 // 줌 배율
        private PointF viewOffset = new PointF(0, 0);   // 이미지 시작 위치

        private const float ZoomStep = 1.1f;            // 휠 한칸에 10%씩 변화
        private const float MinZoom = 0.2f;
        private const float MaxZoom = 5.0f;

        private bool isPanning = false;
        private DPoint lastMousePt;

        /* =========================================================
         *  Constructor
         * ========================================================= */
        public MainForm()
        {
            InitializeComponent();

            loadingTimer = new Timer { Interval = 50 };
            loadingTimer.Tick += (s, e) =>
            {
                spinnerAngle = (spinnerAngle + 20) % 360;
                pictureBoxImage.Invalidate();
            };

            listViewMain.View = View.List;
            listViewMain.FullRowSelect = true;
            listViewMain.HideSelection = false;

            listViewMain.Click += listViewMain_Click;
            listViewMain.DoubleClick += listViewMain_DoubleClick;
            listViewMain.SelectedIndexChanged += listViewMain_SelectedIndexChanged;

            pictureBoxImage.SizeMode = PictureBoxSizeMode.Normal;
            pictureBoxImage.Paint += PictureBoxImage_Paint;
            pictureBoxImage.MouseDown += PictureBoxImage_MouseDown;
            pictureBoxImage.MouseMove += PictureBoxImage_MouseMove;
            pictureBoxImage.MouseUp += PictureBoxImage_MouseUp;
            pictureBoxImage.MouseWheel += PictureBoxImage_MouseWheel;

            pictureBoxPreview.SizeMode = PictureBoxSizeMode.Zoom;
            numCropSize.Value = DefaultCropSize;
            this.FormClosing += MainForm_FormClosing;

            lineContextMenu = new ContextMenuStrip();

            var deleteLineItem = new ToolStripMenuItem("라인 삭제");
            deleteLineItem.Click += (s, e) =>
            {
                if (contextTargetLine != null)
                    DeleteLine(contextTargetLine);
            };

            lineContextMenu.Items.Add(deleteLineItem);

        }

        /* =========================================================
         *  Reset
         * ========================================================= */
        private void BtnReset_Click(object sender, EventArgs e) => ResetAll();

        private void ResetAll()
        {
            // 기준선/점/크롭 전체 제거
            baseLines.Clear();
            currentLine = null;

            // 드래그/hover 상태 초기화
            draggingLine = null;
            dragTarget = DragTarget.None;
            hoveredBox = null;

            // UI 초기화
            ClearPreview();
            ResetView();
            UpdateLineInfo(null);
            listViewMain.Items.Clear();

            //  JSON 파일도 삭제
            if (!string.IsNullOrEmpty(currentImagePath))
            {
                string jsonPath = currentImagePath + ".teaching.json";
                if (File.Exists(jsonPath))
                {
                    File.Delete(jsonPath);
                }
            }


            // Crop size는 기본값으로
            numCropSize.Value = DefaultCropSize;

            pictureBoxImage.Invalidate();
        }

        private void ResetView()   // 이미지 출력 위치 초기화
        {
            viewScale = 1.0f;

            if (viewBitmap != null)
            {
                viewOffset = new PointF(    // 이미지 중앙에 오게
                    (pictureBoxImage.Width - viewBitmap.Width) /2f,
                    (pictureBoxImage.Height - viewBitmap.Height) / 2f 
                );
            }
            else
            {
                viewOffset = new PointF(0, 0);  // 이미지 없으면
            }
        }

        private void NumCropSize_ValueChanged(object sender, EventArgs e)
        {
            if (currentLine != null)
            {
                currentLine.CropSize = (int)numCropSize.Value;
            };
            //UpdateLineInfo();
            pictureBoxImage.Invalidate();
        }

        private void DeleteLine(BaseLineInfo line)
        {
            if (line == null)
                return;

            baseLines.Remove(line);

            // hover / drag 상태 정리
            if (hoveredBox != null && hoveredBox.OwnerLine == line)
                hoveredBox = null;

            if (draggingLine == line)
            {
                draggingLine = null;
                dragTarget = DragTarget.None;
            }

            ClearPreview();
            ShowLineList();
            UpdateLineInfo(null);

            pictureBoxImage.Invalidate();
        }
        private void MainForm_FormClosing(object sender, FormClosingEventArgs e)
        {
            if (!string.IsNullOrEmpty(currentImagePath))
            {
                SaveTeachingData(currentImagePath);
            }
        }


        /* =========================================================
         *  Image Load
         * ========================================================= */

        private async void BtnLoadImage_Click(object sender, EventArgs e)
        {
            OpenFileDialog dlg = new OpenFileDialog
            {
                Filter = "Image Files|*.bmp;*.jpg;*.png"
            };

            if (dlg.ShowDialog() != DialogResult.OK)
                return;

            string newImagePath = dlg.FileName;

            // 1️ 이전 이미지 티칭 정보 저장
            if (!string.IsNullOrEmpty(currentImagePath))
            {
                SaveTeachingData(currentImagePath);
            }

            currentImagePath = newImagePath;

            // 2️ 메모리 상태 초기화
            baseLines.Clear();
            currentLine = null;
            hoveredBox = null;
            draggingLine = null;
            dragTarget = DragTarget.None;
            ClearPreview();
            UpdateLineInfo(null);

            // 3️ 로딩 UI ON
            isImageLoading = true;
            loadingTimer.Start();

            pictureBoxImage.Enabled = false;
            btnCropSave.Enabled = false;
            btnLoadImage.Enabled = false;
            btnReset.Enabled = false;

            Mat loadedMat = null;
            Bitmap viewBmp = null;

            try
            {
                // 4️ 백그라운드: OpenCV로 이미지 로드
                loadedMat = await Task.Run(() =>
                {
                    byte[] bytes = File.ReadAllBytes(currentImagePath);
                    return Cv2.ImDecode(bytes, ImreadModes.Unchanged);
                });

                if (loadedMat.Empty())
                    throw new Exception("이미지를 불러올 수 없습니다.");

                // 5️ UI 스레드: View용 Bitmap 생성
                viewBmp = BitmapConverter.ToBitmap(loadedMat);

                viewBitmap?.Dispose();
                viewBitmap = ResizeToFit(
                    viewBmp,
                    pictureBoxImage.Width,
                    pictureBoxImage.Height
                );

                // 6️ 원본 Mat 교체
                originalMat?.Dispose();

                originalMat = loadedMat;
                loadedMat = null;

                // 이미지 타입 판별
                if (originalMat.Channels() == 1)
                    imageColorInfoText = "Grayscale (CV_8UC1)";
                else if (originalMat.Channels() == 3)
                    imageColorInfoText = "Color (CV_8UC3)";
                else
                    imageColorInfoText = $"Channels: {originalMat.Channels()}";

                // 7️ View 초기화 + 티칭 복원
                ResetView();
                LoadTeachingData(currentImagePath);
                ShowLineList();
            }
            catch (Exception ex)
            {
                MessageBox.Show("이미지 로드 실패: " + ex.Message);
            }
            finally
            {
                viewBmp?.Dispose();
                loadedMat?.Dispose();

                // 8️ 로딩 UI OFF
                isImageLoading = false;
                loadingTimer.Stop();

                pictureBoxImage.Enabled = true;
                btnCropSave.Enabled = true;
                btnLoadImage.Enabled = true;
                btnReset.Enabled = true;

                pictureBoxImage.Invalidate();
            }
        }

        private Bitmap ResizeToFit(Bitmap src, int maxW, int maxH)
        {
            double scale = Math.Min(
                (double)maxW / src.Width,
                (double)maxH / src.Height
            );

            Bitmap dst = new Bitmap(
                (int)(src.Width * scale),
                (int)(src.Height * scale)
            );

            using (Graphics g = Graphics.FromImage(dst))
            {
                g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                g.DrawImage(src, 0, 0, dst.Width, dst.Height);
            }

            return dst;
        }

        private void LoadTeachingData(string imagePath)
        {
            string jsonPath = imagePath + ".teaching.json";

            if (!File.Exists(jsonPath))
                return;

            string json = File.ReadAllText(jsonPath);

            TeachingData data = JsonConvert.DeserializeObject<TeachingData>(json);

            if (data == null || data.Lines == null)
                return;

            baseLines.Clear();

            foreach (var lineData in data.Lines)
            {
                BaseLineInfo line = new BaseLineInfo
                {
                    StartPt = lineData.StartPt,
                    EndPt = lineData.EndPt,
                    CropSize = lineData.CropSize,
                    Anchor = lineData.Anchor
                };

                CalculateCropBoxes(line);
                baseLines.Add(line);
            }

            pictureBoxImage.Invalidate();
        }


        /* =========================================================
         *  Mouse Down
         * ========================================================= */
        private void PictureBoxImage_MouseDown(object sender, MouseEventArgs e)
        {
            if (originalMat == null || viewBitmap == null)
                return;


            if (!IsInsideImageScreen(e.Location))
            {
                MessageBox.Show(
                    "이미지 영역 안을 클릭하세요.",
                    "잘못된 클릭",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Warning
                );
                return;
            }

            switch (e.Button)
            {
                case MouseButtons.Right:
                    {
                        // 1️⃣ 크롭박스 위에서 우클릭했는지 검사
                        PointF ViewPt = ScreenToView(e.Location);
                        PointF OriginalPt = ViewToOriginal(ViewPt);

                        foreach (var line in baseLines)
                        {
                            foreach (var box in line.CropBoxes)
                            {
                                if (box.EffectiveRect.Contains(
                                        (int)OriginalPt.X,
                                        (int)OriginalPt.Y))
                                {
                                    // 🔥 컨텍스트 메뉴 대상 라인 설정
                                    contextTargetLine = line;

                                    // 컨텍스트 메뉴 표시
                                    lineContextMenu.Show(
                                        pictureBoxImage,
                                        e.Location
                                    );
                                    return;
                                }
                            }
                        }

                        // 2️⃣ 크롭박스가 아니면 → 기존 패닝
                        isPanning = true;
                        lastMousePt = e.Location;
                        return;
                    }

                case MouseButtons.Left:

                    // 1️ 기존 기준선 점 드래그 시도
                    foreach (var line in baseLines)
                    {
                        if (IsHitOriginal(mouseOriginalPt, line.StartPt))
                        {
                            draggingLine = line;
                            dragTarget = DragTarget.StartPoint;
                            return;
                        }

                        if (IsHitOriginal(mouseOriginalPt, line.EndPt))
                        {
                            draggingLine = line;
                            dragTarget = DragTarget.EndPoint;
                            return;
                        }
                    }


                    PointF viewPt = ScreenToView(e.Location);
                    PointF originalPt = ViewToOriginal(viewPt);

                    if (clickState == ClickState.None)
                    {
                        // 🔹 새 기준선 시작
                        currentLine = new BaseLineInfo
                        {
                            StartPt = originalPt,
                            CropSize = (int)numCropSize.Value,
                            Anchor = cropAnchor
                        };

                        clickState = ClickState.OnePoint;
                    }
                    else if (clickState == ClickState.OnePoint)
                    {
                        // 🔹 기준선 완성
                        currentLine.EndPt = originalPt;

                        CalculateCropBoxes(currentLine);
                        baseLines.Add(currentLine);
                        ShowLineList();
                        currentLine = null;
                        clickState = ClickState.None;
                    }

                    pictureBoxImage.Invalidate();
                    break;
            }
        }

        /* =========================================================
         *  Mouse Move
         * ========================================================= */
        private void PictureBoxImage_MouseMove(object sender, MouseEventArgs e)
        {
            if (isPanning)    // 이미지 이동
            {
                viewOffset.X += e.X - lastMousePt.X;
                viewOffset.Y += e.Y - lastMousePt.Y;    // 이동거리 계산
                lastMousePt = e.Location;               // (이동한 위치로) 기준점 갱신
                pictureBoxImage.Invalidate();
                return;
            }

            if (draggingLine != null)   // 점 드래그
            {
                PointF viewPt = ScreenToView(e.Location);
                PointF originalPt = ViewToOriginal(viewPt);

                if (dragTarget == DragTarget.StartPoint)
                    draggingLine.StartPt = originalPt;
                else if (dragTarget == DragTarget.EndPoint)
                    draggingLine.EndPt = originalPt;

                CalculateCropBoxes(draggingLine);
                pictureBoxImage.Invalidate();
                return;
            }

            if (!IsInsideImageScreen(e.Location))
                return;

            mouseScreenPt = e.Location;   // 화면에 좌표 표시용
            mouseOriginalPt = ViewToOriginal(ScreenToView(e.Location));  // hover 판정용, preview 대상 결정
            UpdateHoverCropBox(mouseOriginalPt);  // hover 박스 결정, highlight 갱신, preview 갱신
            pictureBoxImage.Invalidate();
        }

        private void PictureBoxImage_MouseUp(object sender, MouseEventArgs e)
        {
            isPanning = false;
            draggingLine = null;
            dragTarget = DragTarget.None;
        }

        private void PictureBoxImage_MouseWheel(object sender, MouseEventArgs e)
        {
            float oldScale = viewScale;   // 확대 전 스케일 저장
            viewScale = e.Delta > 0 ? viewScale * ZoomStep : viewScale / ZoomStep;      // 휠 한칸당 10% 확대/축소
            viewScale = Math.Max(MinZoom, Math.Min(MaxZoom, viewScale));   // 줌 한계 제한

            // 마우스 위치를 기준으로 이미지 다시 배치
            viewOffset.X = e.X - (e.X - viewOffset.X) * (viewScale / oldScale);   // e.X - (기존 거리 × 확대비율)
            viewOffset.Y = e.Y - (e.Y - viewOffset.Y) * (viewScale / oldScale);

            pictureBoxImage.Invalidate();
        }

        /* =========================================================
         *  Paint
         * ========================================================= */
        private void PictureBoxImage_Paint(object sender, PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.Clear(Color.Black);      // 이전 화면 초기화
            g.SmoothingMode = SmoothingMode.AntiAlias;   // 선,원,텍스트 부드럽게

            if (isImageLoading)
            {
                DrawLoadingSpinner(g);
                return;
            }

            if (viewBitmap == null)
                return;

            g.TranslateTransform(viewOffset.X, viewOffset.Y);    // 좌표계 이동(중앙정렬, 드래그 이동) 이미지가 시작하는 위치
            g.ScaleTransform(viewScale, viewScale);              // 좌표계 스케일 변경(확대/축소 적용)

            g.DrawImage(viewBitmap, 0, 0);                       // 이미지 그리기
            DrawPointsAndLine(g);                                // 점, 선 그리기
            DrawGuideBoxes(g);                                   // 가이드 박스 그리기

            g.ResetTransform();                                  // 좌표계 원복
            DrawMousePositionOverlay(g);                         // 마우스 포지션
            DrawImageTypeOverlay(g);
        }

        /* =========================================================
         *  Draw Helpers
         * ========================================================= */
        private void DrawMousePositionOverlay(Graphics g)   // 마우스 포지션 점좌표 그리기
        {
            string text = $"({(int)mouseOriginalPt.X}, {(int)mouseOriginalPt.Y})";

            using (Font font = new Font("맑은 고딕", 9, FontStyle.Bold))
            {
                SizeF size = g.MeasureString(text, font);
                float x = mouseScreenPt.X + 12;      // 마우스 커서랑 겹치지 않게
                float y = mouseScreenPt.Y + 12;

                RectangleF bg = new RectangleF(     // 배경 사각형 크기
                    x, y,
                    size.Width + 8,
                    size.Height + 8
                );
                // 반투명 배경
                //using (Brush b = new SolidBrush(Color.FromArgb(180, 0, 0, 0)))
                //    g.FillRectangle(b, bg);

                g.DrawString(text, font, Brushes.DeepSkyBlue, x + 4, y + 4);    // 텍스트
            }
        }

        private void DrawImageTypeOverlay(Graphics g)
        {
            if (string.IsNullOrEmpty(imageColorInfoText))
                return;

            using (Font font = new Font("맑은 고딕", 9, FontStyle.Bold))
            {
                SizeF size = g.MeasureString(imageColorInfoText, font);

                float x = 8;
                float y = 8;

                RectangleF bg = new RectangleF(
                    x,
                    y,
                    size.Width + 8,
                    size.Height + 8
                    );

                // 반투명 배경
                using (Brush bgBrush = new SolidBrush(Color.FromArgb(160, 0, 0, 0)))
                    g.FillRectangle(bgBrush, bg);

                g.DrawString(
                    imageColorInfoText,
                    font,
                    Brushes.Orange,
                    x + 4,
                    y + 3
                    );
            }
        }
        private void DrawPointsAndLine(Graphics g)
        {
            using (Pen pen = new Pen(Color.Red, 2 / viewScale))
            {
                foreach (var line in baseLines)
                {
                    DrawPoint(g, line.StartPt);
                    DrawPoint(g, line.EndPt);

                    g.DrawLine(
                        pen,
                        OriginalToView(line.StartPt),
                        OriginalToView(line.EndPt)
                    );
                }

                // 아직 완성 안 된 기준선 (currentLine)
                if (currentLine != null)
                {
                    DrawPoint(g, currentLine.StartPt);
                }
            }
        }

        private void DrawPoint(Graphics g, PointF originalPt)
        {
            PointF pt = OriginalToView(originalPt);     // 화면 좌표로
            float r = 4 / viewScale;        // 반지름 보정(확대/축소 해도 똑같게 역보정)
            g.FillEllipse(Brushes.Red, pt.X - r, pt.Y - r, r * 2, r * 2);  // 좌상단 기준이기 때문에 중심으로 보정
        }

        private void DrawGuideBoxes(Graphics g)
        {
            foreach (var line in baseLines)
            {
                foreach (var box in line.CropBoxes)
                {
                    Color color = box.IsHovered ? Color.Lime : Color.Yellow;

                    using (Pen pen = new Pen(color, 2 / viewScale)
                    { DashStyle = DashStyle.Dash })
                    {
                        Rectangle r = box.EffectiveRect;

                        PointF tl = OriginalToView(new PointF(r.Left, r.Top));
                        PointF br = OriginalToView(new PointF(r.Right, r.Bottom));

                        g.DrawRectangle(
                            pen,
                            tl.X,
                            tl.Y,
                            br.X - tl.X,
                            br.Y - tl.Y
                        );
                    }
                }
            }
        }


        /* =========================================================
         *  크롭박스 계산 / 기준점
         * ========================================================= */
        private void CalculateCropBoxes(BaseLineInfo line)
        {
            line.CropBoxes.Clear();

            float dx = line.EndPt.X - line.StartPt.X;
            float dy = line.EndPt.Y - line.StartPt.Y;
            float length = (float)Math.Sqrt(dx * dx + dy * dy);
            if (length < 1f)
                return;

            float ux = dx / length;
            float uy = dy / length;

            int cropSize = line.CropSize;

            for (float dist = 0; dist <= length + cropSize / 2f; dist += cropSize)
            {
                PointF anchor = new PointF(
                    line.StartPt.X + ux * dist,
                    line.StartPt.Y + uy * dist
                );

                PointF tl = AnchorToBox(anchor, cropSize);

                int x = (int)Math.Max(0, Math.Min(tl.X, originalMat.Width - cropSize));
                int y = (int)Math.Max(0, Math.Min(tl.Y, originalMat.Height - cropSize));

                line.CropBoxes.Add(new CropBoxInfo
                {
                    EffectiveRect = new Rectangle(x, y, cropSize, cropSize),
                    OwnerLine = line
                });

            }
        }


        private PointF AnchorToBox(PointF anchor, float size)  // 기준점 계산
        {
            switch (cropAnchor)
            {
                case CropAnchor.Center:
                    return new PointF(anchor.X - size / 2f, anchor.Y - size / 2f);
                case CropAnchor.TopLeft:
                    return anchor;
                case CropAnchor.TopRight:
                    return new PointF(anchor.X - size, anchor.Y);
                case CropAnchor.BottomLeft:
                    return new PointF(anchor.X, anchor.Y - size);
                case CropAnchor.BottomRight:
                    return new PointF(anchor.X - size, anchor.Y - size);
                default:
                    return anchor;
            }
        }

        private void UpdateHoverCropBox(PointF originalPt)
        {
            hoveredBox = null;

            foreach (var line in baseLines)
            {
                foreach (var box in line.CropBoxes)
                {
                    if (box.EffectiveRect.Contains(
                        (int)originalPt.X,
                        (int)originalPt.Y))
                    {
                        hoveredBox = box;
                        break;
                    }
                }
            }

            foreach (var line in baseLines)
                foreach (var box in line.CropBoxes)
                    box.IsHovered = (box == hoveredBox);

            if (hoveredBox != null)
            {
                ShowCropPreview(hoveredBox);
                UpdateLineInfo(hoveredBox.OwnerLine);  // 🔥 핵심
            }
            else
            {
                ClearPreview();
                UpdateLineInfo(null);
            }

        }


        /* =========================================================
         *  Preview 미리보기
         * ========================================================= */
        private void ShowCropPreview(CropBoxInfo crop)
        {
            if (crop == null || originalMat == null)
                return;

            Rectangle r = crop.EffectiveRect;

            // 원본 Mat 기준 ROI
            var roi = new OpenCvSharp.Rect(
                r.X,
                r.Y,
                r.Width,
                r.Height
            );

            using (Mat cropped = new Mat(originalMat, roi))
            {
                pictureBoxPreview.Image?.Dispose();
                pictureBoxPreview.Image = BitmapConverter.ToBitmap(cropped);
            }
        }


        private void ClearPreview()
        {
            pictureBoxPreview.Image?.Dispose();
            pictureBoxPreview.Image = null;
        }

        /* =========================================================
         *  크롭박스 저장
         * ========================================================= */
        private void BtnCropSave_Click(object sender, EventArgs e) => CropAndSaveAll();

        private void CropAndSaveAll()
        {
            if (baseLines.Count == 0)
                return;

            string folder = Path.Combine(
                Application.StartupPath,
                "Crops",
                DateTime.Now.ToString("yyyyMMdd_HHmmss")
            );
            Directory.CreateDirectory(folder);

            int lineIndex = 1;
            int cropIndex = 1;

            foreach (var line in baseLines)
            {
                foreach (var box in line.CropBoxes)
                {
                    Rectangle r = box.EffectiveRect;

                    var roi = new OpenCvSharp.Rect(
                        r.X, r.Y, r.Width, r.Height
                    );

                    string path = Path.Combine(
                        folder,
                        $"L{lineIndex}_C{cropIndex:D3}.png"
                    );

                    using (Mat cropped = new Mat(originalMat, roi))
                    {
                        Cv2.ImWrite(path, cropped);
                    }

                    cropIndex++;
                }
                lineIndex++;
            }
            MessageBox.Show("크롭 이미지 저장 완료");
        }

        private void SaveTeachingData(string imagePath)
        {
            if (baseLines.Count == 0 || string.IsNullOrEmpty(imagePath))
                return;

            TeachingData data = new TeachingData
            {
                ImagePath = imagePath
            };

            foreach (var line in baseLines)
            {
                data.Lines.Add(new BaseLineData
                {
                    StartPt = line.StartPt,
                    EndPt = line.EndPt,
                    CropSize = line.CropSize,
                    Anchor = line.Anchor
                });
            }

            string jsonPath = imagePath + ".teaching.json";
            string json = JsonConvert.SerializeObject(
                data,
                Formatting.Indented
            );

            File.WriteAllText(jsonPath, json);

        }

        /* =========================================================
         *  좌표 계산
         * ========================================================= */
        private PointF ViewToOriginal(PointF viewPt)
        {
            if (originalMat == null || viewBitmap == null)
                return PointF.Empty;

            return new PointF(
                viewPt.X * originalMat.Width / viewBitmap.Width,
                viewPt.Y * originalMat.Height / viewBitmap.Height
            );
        }


        private PointF OriginalToView(PointF originalPt)
        {
            if (originalMat == null || viewBitmap == null)
                return PointF.Empty;

            return new PointF(
                originalPt.X * viewBitmap.Width / originalMat.Width,
                originalPt.Y * viewBitmap.Height / originalMat.Height
            );
        }

        private PointF ScreenToView(DPoint screenPt)
        {
            return new PointF(
                (screenPt.X - viewOffset.X) / viewScale,
                (screenPt.Y - viewOffset.Y) / viewScale
            );
        }

        private DPoint ViewToScreen(PointF viewPt)
        {
            return new DPoint(
                (int)(viewPt.X * viewScale + viewOffset.X),
                (int)(viewPt.Y * viewScale + viewOffset.Y)
            );
        }

        private bool IsHit(DPoint mousePt, DPoint targetPt)
        {
            return Math.Abs(mousePt.X - targetPt.X) <= HitRadius &&
                   Math.Abs(mousePt.Y - targetPt.Y) <= HitRadius;
        }

        private bool IsHitOriginal(PointF originalPt, PointF targetOriginalPt)
        {
            PointF viewPt = OriginalToView(targetOriginalPt);
            DPoint screenPt = ViewToScreen(viewPt);
            return IsHit(mouseScreenPt, screenPt);
        }

        private bool IsInsideImageScreen(DPoint screenPt)
        {
            if (viewBitmap == null)
                return false;

            RectangleF rect = new RectangleF(
                viewOffset.X,
                viewOffset.Y,
                viewBitmap.Width * viewScale,
                viewBitmap.Height * viewScale
            );

            return rect.Contains(screenPt);
        }

        /* =========================================================
         *  List
         * ========================================================= */
        private void ShowLineList()
        {
            listViewMain.BeginUpdate();
            listViewMain.Items.Clear();

            for (int i = 0; i < baseLines.Count; i++)
            {
                baseLines[i].LineIndex = i + 1;

                ListViewItem item = new ListViewItem($"Line {i + 1}");
                item.Tag = baseLines[i];

                listViewMain.Items.Add(item);
            }

            currentListMode = ListViewMode.LineList;
            currentLineInView = null;

            listViewMain.EndUpdate();
        }

        private void ShowCropList(BaseLineInfo line)
        {
            listViewMain.Items.Clear();

            // Back
            ListViewItem back = new ListViewItem("< Back");
            back.Tag = null;
            listViewMain.Items.Add(back);

            for (int i = 0; i < line.CropBoxes.Count; i++)
            {
                ListViewItem item = new ListViewItem($"Crop {i + 1}");
                item.Tag = line.CropBoxes[i];
                listViewMain.Items.Add(item);
            }

            currentListMode = ListViewMode.CropList;
            currentLineInView = line;
            selectedCropBox = null;

            // Crop 리스트 진입 시
            // 라인 전체 하이라이트 유지
            HighlightLine(line);

            pictureBoxImage.Invalidate();
        }

        private void listViewMain_Click(object sender, EventArgs e)
        {
            // 선택된 항목이 없으면 종료
            if (listViewMain.SelectedItems.Count == 0)
                return;

            // 현재 모드가 Line 리스트일 때만
            if (currentListMode != ListViewMode.LineList)
                return;

            ListViewItem item = listViewMain.SelectedItems[0];

            BaseLineInfo line = item.Tag as BaseLineInfo;
            if (line == null)
                return;

            // 현재 선택 라인 갱신
            currentLineInView = line;

            //  해당 라인의 모든 CropBox 하이라이트
            HighlightLine(line);
            UpdateLineInfo(line);

            pictureBoxImage.Invalidate();
        }


        private void listViewMain_DoubleClick(object sender, EventArgs e)
        {
            if (listViewMain.SelectedItems.Count == 0)
                return;

            ListViewItem item = listViewMain.SelectedItems[0];

            // Line → Crop 리스트로 진입
            if (currentListMode == ListViewMode.LineList)
            {
                BaseLineInfo line = item.Tag as BaseLineInfo;
                if (line == null)
                    return;

                ShowCropList(line);
                return;
            }

            // CropList에서는 더블클릭 아무 동작 안 함
        }


        private void listViewMain_SelectedIndexChanged(object sender, EventArgs e)
        {
            if (listViewMain.SelectedItems.Count == 0)
                return;

            ListViewItem item = listViewMain.SelectedItems[0];

            // =========================
            // Crop 리스트 모드
            // =========================
            if (currentListMode == ListViewMode.CropList)
            {
                // Back은 즉시 LineList로
                if (item.Text == "< Back")
                {
                    ShowLineList();
                    return;
                }

                CropBoxInfo crop = item.Tag as CropBoxInfo;
                if (crop == null)
                    return;

                selectedCropBox = crop;

                // 1. 하이라이트
                ClearAllHighlights();
                crop.IsHovered = true;

                // 2. Preview
                ShowCropPreview(crop);

                pictureBoxImage.Invalidate();
                return;
            }

            // =========================
            // Line 리스트 모드
            // =========================
            if (currentListMode == ListViewMode.LineList)
            {
                BaseLineInfo line = item.Tag as BaseLineInfo;
                if (line == null)
                    return;

                currentLineInView = line;
                selectedCropBox = null;

                HighlightLine(line);
                pictureBoxImage.Invalidate();
                return;
            }
        }


        private void ClearAllHighlights()
        {
            foreach (var line in baseLines)
                foreach (var box in line.CropBoxes)
                    box.IsHovered = false;
        }

        private void HighlightLine(BaseLineInfo line)
        {
            ClearAllHighlights();

            if (line == null)
                return;

            foreach (var box in line.CropBoxes)
                box.IsHovered = true;
        }


        /* =========================================================
         *  UI
         * ========================================================= */
        private void UpdateLineInfo(BaseLineInfo line)
        {
            if (line == null)
            {
                lblLineIndex.Text = "Line Index : -";
                lblLineLength.Text = "Line Length : -";
                lblCropCount.Text = "Crop Count : -";
                lblCropSize.Text = "Crop Size : -";
                return;
            }

            // Line Index (1-based)
            int lineIndex = baseLines.IndexOf(line) + 1;

            // Line Length
            float dx = line.EndPt.X - line.StartPt.X;
            float dy = line.EndPt.Y - line.StartPt.Y;
            float length = (float)Math.Sqrt(dx * dx + dy * dy);

            lblLineIndex.Text = $"Line Index : {lineIndex}";
            lblLineLength.Text = $"Line Length : {length:F1}px";

            lblCropCount.Text = $"Crop Count : {line.CropBoxes.Count}";
            lblCropSize.Text = $"Crop Size : {line.CropSize}";
        }



        private void DrawLoadingSpinner(Graphics g)
        {
            int size = 50;
            int x = (pictureBoxImage.Width - size) / 2;
            int y = (pictureBoxImage.Height - size) / 2;

            using (Pen bg = new Pen(Color.FromArgb(50, Color.Gray), 6))
            using (Pen fg = new Pen(Color.DeepSkyBlue, 6))
            {
                g.DrawEllipse(bg, x, y, size, size);
                g.DrawArc(fg, x, y, size, size, spinnerAngle, 100);
            }

            using (Font font = new Font("맑은 고딕", 10, FontStyle.Bold))
            {
                string msg = "Loading...";
                SizeF ts = g.MeasureString(msg, font);
                g.DrawString(
                    msg,
                    font,
                    Brushes.DimGray,
                    (pictureBoxImage.Width - ts.Width) / 2,
                    y + size + 10
                );
            }
        }
    }
}
