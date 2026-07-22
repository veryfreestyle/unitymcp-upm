using FairyGUI;
using LitJson;
using VeryFS.UnityMCP.Editor.Protocol;

namespace VeryFS.UnityMCP.Editor.Commands.FairyGUI
{
    // 按 Unwrap 后的真实 GObject 类型读控件专有状态。
    // null (测试 stub / 无 displayObject) 返空对象，不抛。
    // API 签名已对 FairyGUI 5.2.0 源码逐一核对 (Task 5):
    //   GObject.alpha: float  -> cast to double for LitJson
    //   GSlider/GProgressBar.value/min/max: double -> no cast
    //   ScrollPane.percX/percY/contentWidth/contentHeight: float -> cast to double
    public sealed class FairyGUIWidgetStateReader : IWidgetStateReader
    {
        public JsonData ReadWidgetState(IUINode node)
        {
            var result = JsonRpcSerializer.Object();
            var obj = node?.Unwrap();
            if (obj == null)
            {
                return result;
            }

            // 通用 GObject 状态
            // GObject.cs:793  public bool touchable
            result["touchable"] = obj.touchable;
            // GObject.cs:835  public bool enabled
            result["enabled"] = obj.enabled;
            // GObject.cs:1009 public bool focused
            result["focused"] = obj.focused;
            // GObject.cs:903  public float alpha -> cast to double
            result["alpha"] = (double)obj.alpha;
            // GObject.cs:1439 public bool draggable
            result["draggable"] = obj.draggable;

            switch (obj)
            {
                case GButton btn:
                    // GButton.cs:221 public bool selected
                    result["selected"] = btn.selected;
                    // GButton.cs:101 public string title
                    result["title"] = btn.title ?? string.Empty;
                    break;

                case GList list:
                    // GList.cs:457   public int selectedIndex
                    result["selectedIndex"] = list.selectedIndex;
                    // GList.cs:1508  public int numItems
                    result["numItems"] = list.numItems;
                    // GList.cs:36    public ListSelectionMode selectionMode (field)
                    result["selectionMode"] = list.selectionMode.ToString();
                    // GList.cs:516   public List<int> GetSelection()
                    var sel = list.GetSelection();
                    var selArr = new JsonData();
                    selArr.SetJsonType(JsonType.Array);
                    foreach (var i in sel) { selArr.Add(i); }
                    result["selection"] = selArr;
                    break;

                case GComboBox combo:
                    // GComboBox.cs:268 public int selectedIndex
                    result["selectedIndex"] = combo.selectedIndex;
                    // GComboBox.cs:309 public string value
                    result["value"] = combo.value ?? string.Empty;
                    // GComboBox.cs:170 public string[] items
                    var items = combo.items;
                    var itemsArr = new JsonData();
                    itemsArr.SetJsonType(JsonType.Array);
                    if (items != null) { foreach (var it in items) { itemsArr.Add(it); } }
                    result["items"] = itemsArr;
                    break;

                case GSlider slider:
                    // GSlider.cs:122 public double value
                    result["value"] = slider.value;
                    // GSlider.cs:84  public double min
                    result["min"] = slider.min;
                    // GSlider.cs:103 public double max
                    result["max"] = slider.max;
                    break;

                case GProgressBar bar:
                    // GProgressBar.cs:96 public double value
                    result["value"] = bar.value;
                    // GProgressBar.cs:58 public double min
                    result["min"] = bar.min;
                    // GProgressBar.cs:77 public double max
                    result["max"] = bar.max;
                    break;

                case GTextInput input:
                    // GTextInput.cs:99  public string promptText
                    result["promptText"] = input.promptText ?? string.Empty;
                    // GTextInput.cs:45  public bool editable
                    result["editable"] = input.editable;
                    // GTextInput.cs:63  public int maxLength
                    result["maxLength"] = input.maxLength;
                    // GTextInput.cs:81  public bool displayAsPassword
                    result["displayAsPassword"] = input.displayAsPassword;
                    break;
            }

            // 滚动信息: 任意含 scrollPane 的 GComponent (含 GList/GComboBox)
            // GComponent.cs:27 public ScrollPane scrollPane { get; private set; }
            if (obj is GComponent comp && comp.scrollPane != null)
            {
                var sp = comp.scrollPane;
                // ScrollPane.cs:443  public float percX       -> cast to double
                result["percX"] = (double)sp.percX;
                // ScrollPane.cs:463  public float percY       -> cast to double
                result["percY"] = (double)sp.percY;
                // ScrollPane.cs:541  public bool isBottomMost
                result["isBottomMost"] = sp.isBottomMost;
                // ScrollPane.cs:549  public bool isRightMost
                result["isRightMost"] = sp.isRightMost;
                // ScrollPane.cs:667  public float contentWidth  -> cast to double
                result["contentWidth"] = (double)sp.contentWidth;
                // ScrollPane.cs:678  public float contentHeight -> cast to double
                result["contentHeight"] = (double)sp.contentHeight;
            }

            return result;
        }
    }
}
