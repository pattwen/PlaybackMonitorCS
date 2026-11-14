using System.ComponentModel.Design.Serialization;
using System.Diagnostics.Eventing.Reader;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using Monitor.Core;

namespace Monitor.App;
public sealed class ObjectEditor<T> : Window where T : class
{
    T value;
    readonly Dictionary<PropertyInfo, FrameworkElement> inputs = [];
    public T? Result { get;
        private set;
    }

    public ObjectEditor(string title, T original, Action<T> validate, Window owner, AppSettings? config = null, string? configPath = null)
    {
        SetResourceReference(StyleProperty, typeof(Window));
        Owner = owner;
        Title = title;
        Width = 620;
        Height = 690;

        MaxWidth = 620;
        MaxHeight = 690;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        value = JsonSerializer.Deserialize<T>(JsonSerializer.Serialize(original, Configuration.Json), Configuration.Json)!;
        DockPanel root = new DockPanel
        {
            Margin = new Thickness(16)
        };
        Content = root;
        StackPanel buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right
        };
        DockPanel.SetDock(buttons, Dock.Bottom);
        root.Children.Add(buttons);
        Button save = new Button
        {
            Content = "确定"
        };
        Button cancel = new Button
        {
            Content = "取消"
        };
        Button reset = new Button
        {
            Content = "重置"
        };
        reset.Visibility = value is DetectionSettings ? Visibility.Visible : Visibility.Collapsed;
        reset.ToolTip = "恢复本窗口中的默认检测参数，点击确定后保存生效；取消则不修改。";
        buttons.Children.Add(save);
        buttons.Children.Add(cancel);
        buttons.Children.Add(reset);
        cancel.Click += (_, _) => DialogResult = false;
        StackPanel form = new StackPanel();
        root.Children.Add(new ScrollViewer { Content = form, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
        var rows = new Dictionary<string, DockPanel>();
        var sections = new List<(DetectionEditorGroup Group, Expander Expander, TextBlock Description)>();
        var properties = typeof(T).GetProperties().Where(property => property.CanWrite && property.Name != "Id").ToArray();
        if (value is DetectionSettings)
        {
            foreach (var group in DetectionEditorLayout.Groups)
            {
                var content = new StackPanel();
                var description = new TextBlock
                {
                    Text = group.Description,
                    TextWrapping = TextWrapping.Wrap,
                    Margin = new Thickness(0, 4, 0, 8),
                    Opacity = .8
                };
                content.Children.Add(description);
                var expander = new Expander
                {
                    Header = group.Title,
                    IsExpanded = true,
                    Content = content,
                    Margin = new Thickness(0, 6, 0, 10)
                };
                sections.Add((group, expander, description));
                form.Children.Add(expander);
                foreach (string name in group.Properties)
                {
                    var property = properties.Single(property => property.Name == name);
                    AddInput(property, content);
                }
            }
        }

        foreach (var property in properties.Where(property => !inputs.ContainsKey(property)))
        {
            AddInput(property, form);
        }

        void AddInput(PropertyInfo property, Panel parent)
        {
            var row = new DockPanel
            {
                Margin = new Thickness(0, 4, 0, 4)
            };
            var label = new TextBlock
            {
                Text = Label(property.Name),
                Width = 250,
                VerticalAlignment = VerticalAlignment.Center,
                TextWrapping = TextWrapping.Wrap,
                ToolTip = Help(property.Name)
            };
            row.Children.Add(label);
            FrameworkElement edit;
            if (value is DetectionSettings && property.Name == nameof(DetectionSettings.VisualLocalEnabled))
            {
                edit = new ComboBox
                {
                    ItemsSource = new[]
                    {
                        new KeyValuePair<bool, string>(true, "局部区域检测"),
                        new KeyValuePair<bool, string>(false, "旧版全局检测")
                    },
                    DisplayMemberPath = "Value",
                    SelectedValuePath = "Key",
                    SelectedValue = property.GetValue(value)
                };
            }
            else
            {
                if (property.PropertyType == typeof(bool))
                {
                    edit = new CheckBox
                    {
                        IsChecked = (bool)property.GetValue(value)!
                    };
                }
                else
                {
                    if (property.PropertyType == typeof(VisualSensitivity))
                    {
                        edit = new ComboBox
                        {
                            ItemsSource = new[]
                            {
                                new KeyValuePair<VisualSensitivity, string>(VisualSensitivity.Conservative, "保守"),
                                new KeyValuePair<VisualSensitivity, string>(VisualSensitivity.Normal, "标准"),
                                new KeyValuePair<VisualSensitivity, string>(VisualSensitivity.Sensitive, "灵敏")
                            },
                            DisplayMemberPath = "Value",
                            SelectedValuePath = "Key",
                            SelectedValue = property.GetValue(value)
                        };
                    }
                    else
                    {
                        if (property.PropertyType.IsEnum)
                        {
                            edit = new ComboBox
                            {
                                ItemsSource = Enum.GetValues(property.PropertyType),
                                SelectedItem = property.GetValue(value)
                            };
                        }
                        else
                        {
                            edit = new TextBox
                            {
                                Text = property.GetValue(value)is int[] channels ? string.Join(",", channels) : Convert.ToString(property.GetValue(value), CultureInfo.InvariantCulture) ?? ""
                            };
                        }
                    }
                }
            }

            edit.ToolTip = Help(property.Name);
            inputs[property] = edit;
            rows[property.Name] = row;
            row.Children.Add(edit);
            parent.Children.Add(row);
        }

        bool Selected(string property)
        {
            var input = inputs.First(pair => pair.Key.Name == property).Value;
            return input is CheckBox checkbox ? checkbox.IsChecked == true : ((ComboBox)input).SelectedValue is true;
        }

        void RefreshEnabled()
        {
            if (value is not DetectionSettings)
            {
                return;
            }

            bool local = Selected(nameof(DetectionSettings.VisualLocalEnabled));
            foreach (var(name, row)in rows)
            {
                bool enabled = DetectionEditorLayout.Enabled(name, Selected);
                row.IsEnabled = enabled;
                row.Opacity = enabled ? 1 : .45;
            }

            foreach (var(group, expander, description)in sections)
            {
                bool active = !group.LocalMode.HasValue || group.LocalMode.Value == local;
                expander.Header = group.Title + (active ? "" : "（当前未启用）");
                description.Text = active ? group.Description : "当前检测模式不使用本组参数；已填写的值会保留。";
            }
        }

        foreach (var input in inputs.Values)
        {
            if (input is CheckBox checkbox)
            {
                checkbox.Checked += (_, _) => RefreshEnabled();
                checkbox.Unchecked += (_, _) => RefreshEnabled();
            }
        }

        if (value is DetectionSettings)
        {
            ((ComboBox)inputs.Single(pair => pair.Key.Name == nameof(DetectionSettings.VisualLocalEnabled)).Value).SelectionChanged += (_, _) => RefreshEnabled();
        }

        RefreshEnabled();
        save.Click += (_, _) =>
        {
            try
            {
                foreach (var(p, edit)in inputs)
                {
                    object? converted = edit is CheckBox checkbox ? checkbox.IsChecked == true : edit is ComboBox combo ? combo.SelectedValue : p.PropertyType == typeof(int[]) ? ((TextBox)edit).Text.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(int.Parse).ToArray() : Convert.ChangeType(((TextBox)edit).Text, p.PropertyType, CultureInfo.InvariantCulture);
                    p.SetValue(value, converted);
                }

                validate(value);
                Result = value;
                DialogResult = true;
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, ex.InnerException?.Message ?? ex.Message, "参数无效");
            }
        };
        reset.Click += (_, _) =>
        {
            try
            {
                if (value is not DetectionSettings)
                {
                    return;
                }

                value = (T)(object)new DetectionSettings();
                validate(value);
                foreach (var(property, control)in inputs)
                {
                    object? restored = property.GetValue(value);
                    if (control is CheckBox checkbox)
                    {
                        checkbox.IsChecked = (bool)restored!;
                    }
                    else
                    {
                        if (control is ComboBox combo)
                        {
                            combo.SelectedValue = restored;
                        }
                        else
                        {
                            if (control is TextBox text)
                            {
                                text.Text = restored is int[] channels ? string.Join(",", channels) : Convert.ToString(restored, CultureInfo.InvariantCulture) ?? "";
                            }
                        }
                    }
                }

                RefreshEnabled();
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, $"重置失败:{ex.Message}");
            }
        };
    }

    static string? Help(string key) => key switch
    {
        "GrayEnabled" => "独立的疑似灰屏检测开关，局部与旧版全局模式均可使用；不依赖静帧检测。",
        "GrayMinAreaPercent" => "相连且颜色接近的平滑中性灰区域占有效ROI的最小比例。默认80%；降低可识别局部灰色填充，也更容易把正常灰墙等背景列为候选。",
        "GrayChromaLimit" => "RGB最大与最小通道差的上限，按0～255归一化计算；越小越接近中性灰。会同时检查块内色彩变化，避免互补颜色平均后被当灰色。",
        "GrayUniformity" => "区域亮度/色彩标准差上限，按0～255归一化计算。越小越要求颜色均匀；同时检查单个16×16块及整个相连区域，抑制纹理和渐变。",
        "GrayMinLevel" => "灰屏候选的最低亮度，按0～255归一化信号刻度，不是nits。默认24，用于排除近黑区域。",
        "GrayMaxLevel" => "灰屏候选的最高亮度，按0～255归一化信号刻度，不是nits。默认235，用于排除近白区域。",
        "GraySeconds" => "普通灰屏候选在同一区域连续存在的媒体时间，达到该时长才告警；普通候选中间消失会重新累计。与局部检测命中次数独立。",
        "GrayRecordBrief" => "有上一采样参考，且原有纹理突然被足够大且均匀的灰色区域替代时，允许单帧记录；普通候选仍需满足灰屏持续时间。仅能记录实际采样到的异常帧。",
        "VisualLocalEnabled" => "启用局部、多尺度视觉检测；关闭后使用全局马赛克/花屏规则。局部模式不使用全局分数和连续1.5秒门限。",
        "VisualSensitivity" => "标准为初始预设；保守减少候选，灵敏增加候选。节目特效可能与真实损坏外观相同。",
        "VisualMinAreaPercent" => "候选区域占有效ROI的最小比例。区域为算法观察窗口，不等于逐像素坏点面积。过大可能使小范围异常无法记录。",
        "VisualRecordBrief" => "多个视觉特征较强且有非切镜时间变化证据时，允许单帧记录。普通候选仍须满足窗口内次数。",
        "VisualConfirmWindowSeconds" => "在此媒体时间窗口内对同一局部区域累计异常，不同位置的候选不会合并次数。低采样率下自动延长到至少能容纳设定次数。",
        "VisualMinimumHits" => "窗口内同一区域至少多少次采样存在候选才确认事件；25次/秒时3次命中最快约80毫秒，不等于持续整个累计窗口。数值越大越保守；确认后由连续正常恢复时间结束。",
        "GreenEnabled" => "识别绿色集中且纹理很少的局部或整幅画面；绿幕、草地和灯光仍需人工复核。仅局部模式生效。",
        "VisualGreenAreaPercent" => "相连的平滑绿色区域占ROI的最小面积；调低可识别更小的绿色补偿块，也会增加正常绿幕/灯光候选。",
        "Name" => "当前输入源或任务的唯一识别名称，用于日志和告警标识。",
        "Location" => "媒体输入源的完整路径，支持本地文件路径、RTSP/RTMP/HTTP 等网络流媒体地址。",
        "IsLive" => "指示输入源是否为实时直播流。若勾选则按流处理（忽略文件结束和部分seek操作）；若取消勾选则按本地文件循环/顺序处理。",
        "NetworkCacheMs" => "底层流媒体库（如 FFmpeg）的网络接收缓冲区大小（毫秒）。增大有助于抗网络抖动，但会增加实时播放/检测延迟。",
        "OpenTimeoutSeconds" => "连接或打开媒体源的最大等待超时时间（秒）。超时未成功读取到数据流将触发连接异常。",
        "RtspTransport" => "指定 RTSP 流的传输协议，常见选项有 tcp（更稳定抗丢包）或 udp（低延迟但可能花屏）。",
        "HardwareDecode" => "指示是否优先启用 GPU 硬件加速解码（如 NVDEC、QSV、DXVA2），可显著降低 CPU 占用率。",
        "AnalysisFps" => $"设置每秒分析的视频帧数，新配置默认{new DetectionSettings().AnalysisFps:0.##}，上限60。实际分析频率受素材帧率、硬件和负载影响。已有配置保留保存值；重置恢复当前程序内定义的默认值。",
        "BlackLevel" => "SDR黑像素的亮度判定上限（取值0～255）。用于定义标准动态范围下何种灰度判定为纯黑。",
        "BlackRatio" => "黑像素在单帧画面中的面积占比阈值（%）。全帧黑像素占比达到该值时，判定当前帧为黑画面。",
        "HdrBlackNits" => "HDR黑像素的绝对亮度判定上限（单位：nits）。用于高动态范围下精确定义黑电平的临界亮度。",
        "BlackSeconds" => "黑画面状态的连续维持时间阈值（秒）。满足该时长后触发黑场事件；中途若画面亮起则重置计数。",
        "FreezeDifference" => "全局平均亮度变化上限，越小越严格。该值不是运动像素的百分比。",
        "FreezeSeconds" => "静帧状态连续维持的时间阈值（秒）。满足该时长后触发画面卡死/静帧告警。",
        "FreezeLocalMotionProtection" => "逐像素比较上一采样画面与较早参考画面；局部运动达到阈值时暂停静帧累计。关闭可恢复原全局亮度差判定，一般不开启此选项。",
        "FreezePixelDifference" => "某像素RGB最大通道差超过此值，才计为变化像素；越小越容易识别微小变化，也越容易受噪声影响。按0～255标尺计算，支持小数。",
        "FreezeBlockChangedPercent" => "16×16块中至少多少百分比的有效像素发生变化，才算运动块；提高可过滤零散噪声。",
        "FreezeMotionAreaPercent" => "运动块覆盖ROI的比例达到此值时认为有局部运动；0.02表示0.02%。越小越能识别小范围运动，但时钟/台标也可能阻止静帧告警。",
        "FreezeReferenceSeconds" => "较早参考画面的更新间隔；增加可帮助识别缓慢运动。实际取相邻采样时刻，受图像采样频率限制。",
        "FreezeMotionHoldSeconds" => "检测到局部运动后，持续这么多秒视为正常运动，避免间歇的小幅运动立刻重新累计静帧。",
        "MosaicScore" => "仅旧版全局模式生效。局部模式使用灵敏度预设；降低旧版分数不会影响局部检测。",
        "MosaicSeconds" => "仅旧版全局模式生效的连续马赛克持续时间；局部模式使用同区域命中窗口。",
        "FlowerScore" => "仅旧版全局模式生效。局部模式综合局部纹理、色彩及时间变化，不使用此全局噪声阈值。",
        "FlowerSeconds" => "仅旧版全局模式生效的连续花屏持续时间；局部模式使用同区域命中窗口。",
        "SilenceDb" => "静音判定的分贝阈值（dBFS）。低于此音量电平的音频将被判定为静音。",
        "SilenceSeconds" => "静音状态连续维持的时间阈值（秒）。满足该时长后触发静音告警。",
        "AudioGapSeconds" => "疑似音频间断（断流或突发空白）的时间阈值（秒）。超过该间隔将判定音频流出现间断。",
        "LossSeconds" => "音视频数据流完全中断的超时阈值（秒）。超过该时间未收到任何音视频包将判定为中断。",
        "RecoverySeconds" => "异常状态解除后，需要连续保持正常状态达到的时间（秒）。只有达到该时间才正式确认故障已恢复。",
        "RoiX" => "分析区域（ROI）左上角相对于画面的水平起始位置百分比（0%～100%）。",
        "RoiY" => "分析区域（ROI）左上角相对于画面的垂直起始位置百分比（0%～100%）。",
        "RoiWidth" => "分析区域（ROI）的宽度占画面总宽度的百分比。",
        "RoiHeight" => "分析区域（ROI）的高度占画面总高度的百分比。",
        "SilentChannels" => "指定逐声道静音检测的声道索引。以 0 起始编号，使用英文逗号分隔（例如 0,1）；若留空则检测整体混合音轨。",
        "BlackEnabled" => "总开关：是否启用黑画面（黑场）检测功能。",
        "FreezeEnabled" => "总开关：是否启用静帧（画面卡死）检测功能。",
        "MosaicEnabled" => "总开关：是否启用疑似马赛克画面检测功能。",
        "FlowerEnabled" => "总开关：是否启用疑似花屏画面检测功能。",
        "SilenceEnabled" => "总开关：是否启用静音检测功能。",
        "AudioGapEnabled" => "总开关：是否启用疑似音频间断检测功能。",
        "DecodeEnabled" => "总开关：是否启用解码错误提示与告警功能。",
        "LossEnabled" => "总开关：是否启用音视频中断（断流）检测功能。",
        _ => null
    };
    static string Label(string key) => key switch
    {
        "GrayEnabled" => "疑似灰屏检测",
        "GrayMinAreaPercent" => "灰色区域最小面积（%）",
        "GrayChromaLimit" => "中性色差上限（0～255）",
        "GrayUniformity" => "区域离散度上限（0～255）",
        "GrayMinLevel" => "灰色亮度下限（0～255）",
        "GrayMaxLevel" => "灰色亮度上限（0～255）",
        "GraySeconds" => "灰屏持续（秒）",
        "GrayRecordBrief" => "记录突发的短暂灰屏",
        "VisualLocalEnabled" => "画面异常检测模式",
        "VisualSensitivity" => "画面异常灵敏度",
        "VisualMinAreaPercent" => "候选区域最小面积（%）",
        "VisualRecordBrief" => "记录短暂的较强异常",
        "VisualConfirmWindowSeconds" => "异常累计窗口（秒）",
        "VisualMinimumHits" => "窗口内异常采样次数",
        "GreenEnabled" => "疑似绿屏检测",
        "VisualGreenAreaPercent" => "平滑绿色区域最小面积（%）",
        "Name" => "名称",
        "Location" => "文件路径 / 媒体地址",
        "IsLive" => "实时输入（文件取消勾选）",
        "NetworkCacheMs" => "网络缓冲参数（ms）",
        "OpenTimeoutSeconds" => "打开/读取超时（秒）",
        "RtspTransport" => "RTSP传输 tcp / udp",
        "HardwareDecode" => "优先硬件解码",
        "AnalysisFps" => "图像采样频率（1～60）",
        "BlackLevel" => "SDR黑像素亮度上限（0～255）",
        "BlackRatio" => "黑像素占比（%）",
        "HdrBlackNits" => "HDR黑像素亮度上限（nits）",
        "BlackSeconds" => "黑画面持续（秒）",
        "FreezeDifference" => "静帧全局亮度差（0～20）",
        "FreezeSeconds" => "静帧持续（秒）",
        "FreezeLocalMotionProtection" => "静帧：局部运动保护",
        "FreezePixelDifference" => "运动像素变化下限（0～255）",
        "FreezeBlockChangedPercent" => "块内变化像素下限（%）",
        "FreezeMotionAreaPercent" => "运动块覆盖画面下限（%）",
        "FreezeReferenceSeconds" => "局部变化参考间隔（秒）",
        "FreezeMotionHoldSeconds" => "检测到运动后保持（秒）",
        "MosaicScore" => "旧版：马赛克块边界分数",
        "MosaicSeconds" => "旧版：马赛克持续（秒）",
        "FlowerScore" => "旧版：花屏噪声分数",
        "FlowerSeconds" => "旧版：花屏持续（秒）",
        "SilenceDb" => "静音阈值（dBFS）",
        "SilenceSeconds" => "静音持续（秒）",
        "AudioGapSeconds" => "疑似音频间断阈值（秒）",
        "LossSeconds" => "音视频中断超时（秒）",
        "RecoverySeconds" => "连续正常恢复时间（秒）",
        "RoiX" => "ROI左侧（%）",
        "RoiY" => "ROI顶部（%）",
        "RoiWidth" => "ROI宽度（%）",
        "RoiHeight" => "ROI高度（%）",
        "SilentChannels" => "逐声道静音：0起始编号，逗号分隔；留空检测整体",
        "BlackEnabled" => "黑画面检测",
        "FreezeEnabled" => "静帧检测",
        "MosaicEnabled" => "疑似马赛克检测",
        "FlowerEnabled" => "疑似花屏检测",
        "SilenceEnabled" => "静音检测",
        "AudioGapEnabled" => "疑似音频间断检测",
        "DecodeEnabled" => "解码错误提示",
        "LossEnabled" => "音视频中断检测",
        _ => key
    };
}

public sealed class SourceWindow : Window
{
    readonly ListBox list = new();
    readonly List<SourceProfile> profiles;
    public SourceProfile? Selected { get;
        private set;
    }
    public List<SourceProfile> Profiles => profiles;

    public SourceWindow(List<SourceProfile> sources, Window owner)
    {
        SetResourceReference(StyleProperty, typeof(Window));
        Owner = owner;
        Title = "地址 / 文件配置";
        Width = 760;
        Height = 470;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        profiles = [..sources];
        var root = new DockPanel
        {
            Margin = new Thickness(16)
        };
        Content = root;
        var note = new TextBlock
        {
            Text = "地址中的账号会保存在本机配置中；告警记录隐藏账号和查询参数。",
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 10)
        };
        DockPanel.SetDock(note, Dock.Top);
        root.Children.Add(note);
        var buttons = new WrapPanel
        {
            Margin = new Thickness(0, 10, 0, 0)
        };
        DockPanel.SetDock(buttons, Dock.Bottom);
        root.Children.Add(buttons);
        root.Children.Add(list);
        void Refresh()
        {
            int selected = list.SelectedIndex;
            list.ItemsSource = null;
            list.ItemsSource = profiles.Select(p => $"{p.Name}  |  {(p.IsLive ? "直播" : "文件")}\n{Text.SafeSource(p.Location)}").ToArray();
            if (profiles.Count > 0)
            {
                list.SelectedIndex = Math.Clamp(selected, 0, profiles.Count - 1);
            }
        }

        void Add(string title, Action action)
        {
            var b = new Button
            {
                Content = title
            };
            b.Click += (_, _) => action();
            buttons.Children.Add(b);
        }

        void Edit(bool create)
        {
            int index = list.SelectedIndex;
            if (!create && index < 0)
            {
                return;
            }

            var editor = new ObjectEditor<SourceProfile>(create ? "新增输入" : "编辑输入", create ? new() { IsLive = true } : profiles[index], p => p.Validate(), this);
            if (editor.ShowDialog() == true)
            {
                if (create)
                {
                    profiles.Add(editor.Result!);
                }
                else
                {
                    profiles[index] = editor.Result!;
                }

                Refresh();
            }
        }

        Add("新增", () => Edit(true));
        Add("编辑", () => Edit(false));
        Add("删除", () =>
        {
            if (list.SelectedIndex >= 0)
            {
                profiles.RemoveAt(list.SelectedIndex);
                Refresh();
            }
        });
        Add("保存并关闭", () =>
        {
            DialogResult = true;
        });
        Add("保存并播放", () =>
        {
            if (list.SelectedIndex >= 0)
            {
                Selected = profiles[list.SelectedIndex];
                DialogResult = true;
            }
        });
        Add("取消", () => DialogResult = false);
        list.MouseDoubleClick += (_, _) =>
        {
            if (list.SelectedIndex >= 0)
            {
                Selected = profiles[list.SelectedIndex];
                DialogResult = true;
            }
        };
        Refresh();
    }
}

public sealed class StreamUrlWindow : Window
{
    public SourceProfile? Result { get;
        private set;
    }

    public StreamUrlWindow(SourceProfile source, Window owner)
    {
        SetResourceReference(StyleProperty, typeof(Window));
        Owner = owner;
        Title = "直播链接";
        Width = 760;
        Height = 260;
        MaxWidth = 760;
        MaxHeight = 260;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var root = new DockPanel
        {
            Margin = new Thickness(16)
        };
        Content = root;
        var buttons = new WrapPanel
        {
            Margin = new Thickness(0, 15, 0, 0),
            HorizontalAlignment = HorizontalAlignment.Right
        };
        var play = new Button
        {
            Content = "播放",
            Width = 90,
            Height = 30,
            Margin = new Thickness(0, 0, 10, 0),
            Background = System.Windows.Media.Brushes.DarkCyan,
            Foreground = System.Windows.Media.Brushes.White
        };
        var cancle = new Button
        {
            Content = "取消",
            Width = 80,
            Height = 30
        };
        var formPanel = new StackPanel
        {
            Orientation = Orientation.Vertical
        };
        var playName = new TextBox
        {
            Text = source?.Name ?? "新直播",
            Margin = new Thickness(0, 5, 0, 15),
            Height = 28,
            VerticalContentAlignment = VerticalAlignment.Center
        };
        var playUrl = new TextBox
        {
            Text = source?.Location ?? "",
            Margin = new Thickness(0, 5, 0, 15),
            Height = 28,
            VerticalContentAlignment = VerticalAlignment.Center
        };
        formPanel.Children.Add(new TextBlock { Text = "名称:", FontWeight = FontWeights.Bold });
        formPanel.Children.Add(playName);
        formPanel.Children.Add(new TextBlock { Text = "直播地址:", FontWeight = FontWeights.Bold });
        formPanel.Children.Add(playUrl);
        play.Click += (s, e) =>
        {
            if (string.IsNullOrWhiteSpace(playUrl.Text))
            {
                MessageBox.Show(this, "请输入有效的媒体播放地址", "参数无效", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            try
            {
                var result = (source ?? new SourceProfile())with
                {
                    Name = string.IsNullOrWhiteSpace(playName.Text) ? "新直播" : playName.Text.Trim(),
                    Location = playUrl.Text.Trim(),
                    IsLive = true
                };
                result.Validate();
                Result = result;
                DialogResult = true;
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, ex.InnerException?.Message ?? ex.Message, "参数无效", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        };
        cancle.Click += (s, e) =>
        {
            DialogResult = false;
        };
        buttons.Children.Add(play);
        buttons.Children.Add(cancle);
        DockPanel.SetDock(buttons, Dock.Bottom);
        root.Children.Add(buttons);
        root.Children.Add(formPanel);
    }
}

public static class Prompt
{
    public static (bool Ok, string Text) Get(Window owner, string title, string initial = "")
    {
        var window = new Window
        {
            Owner = owner,
            Title = title,
            Width = 520,
            Height = 190,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            ResizeMode = ResizeMode.NoResize
        };
        var root = new StackPanel
        {
            Margin = new Thickness(16)
        };
        var text = new TextBox
        {
            Text = initial,
            Margin = new Thickness(0, 0, 0, 16)
        };
        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right
        };
        var ok = new Button
        {
            Content = "确定",
            IsDefault = true
        };
        var cancel = new Button
        {
            Content = "取消",
            IsCancel = true
        };
        buttons.Children.Add(ok);
        buttons.Children.Add(cancel);
        root.Children.Add(text);
        root.Children.Add(buttons);
        window.Content = root;
        ok.Click += (_, _) => window.DialogResult = true;
        return (window.ShowDialog() == true, text.Text);
    }
}
