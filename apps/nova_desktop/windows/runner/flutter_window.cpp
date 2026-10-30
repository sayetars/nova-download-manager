#include "flutter_window.h"

#include <optional>
#include <string>
#include <vector>

#include <shellapi.h>
#include <shlwapi.h>

#include "flutter/generated_plugin_registrant.h"
#include "nova_pipe_bridge.h"
#include "resource.h"

FlutterWindow::FlutterWindow(const flutter::DartProject& project)
    : project_(project) {}

FlutterWindow::~FlutterWindow() {}

bool FlutterWindow::OnCreate() {
  if (!Win32Window::OnCreate()) {
    return false;
  }

  RECT frame = GetClientArea();

  // The size here must match the window dimensions to avoid unnecessary surface
  // creation / destruction in the startup path.
  flutter_controller_ = std::make_unique<flutter::FlutterViewController>(
      frame.right - frame.left, frame.bottom - frame.top, project_);
  // Ensure that basic setup of the controller was successful.
  if (!flutter_controller_->engine() || !flutter_controller_->view()) {
    return false;
  }
  RegisterPlugins(flutter_controller_->engine());
  SetChildContent(flutter_controller_->view()->GetNativeWindow());

  // Nova engine IPC bridge (named pipe ↔ method channel "nova/pipe").
  pipe_bridge_ = std::make_unique<nova::PipeBridge>(
      flutter_controller_->engine()->messenger());

  // Nova drag & drop bridge: OS drops (files + text/URLs) forwarded on
  // method channel "nova/dnd" as a call to "onDrop" with a string list.
  dnd_channel_ = std::make_unique<flutter::MethodChannel<flutter::EncodableValue>>(
      flutter_controller_->engine()->messenger(), "nova/dnd",
      &flutter::StandardMethodCodec::instance());
  DragAcceptFiles(GetHandle(), TRUE);

  // v1.8.0 (IDM tray parity): "nova/tray" — init creates the tray icon,
  // hide/show minimize-to-tray and restore. The menu and left click notify
  // Dart via "onTrayShow" / "onTrayExit".
  tray_channel_ = std::make_unique<flutter::MethodChannel<flutter::EncodableValue>>(
      flutter_controller_->engine()->messenger(), "nova/tray",
      &flutter::StandardMethodCodec::instance());
  tray_channel_->SetMethodCallHandler(
      [this](const flutter::MethodCall<flutter::EncodableValue>& call,
             std::unique_ptr<flutter::MethodResult<flutter::EncodableValue>> result) {
        if (call.method_name() == "init") {
          const bool ok = TrayCreate();
          result->Success(flutter::EncodableValue(ok));
        } else if (call.method_name() == "hide") {
          ::ShowWindow(GetHandle(), SW_HIDE);
          result->Success(flutter::EncodableValue(true));
        } else if (call.method_name() == "show") {
          ::ShowWindow(GetHandle(), SW_SHOW);
          ::SetForegroundWindow(GetHandle());
          result->Success(flutter::EncodableValue(true));
        } else {
          result->NotImplemented();
        }
      });

  // v1.8.0 (IDM "drag files out of the app" parity): "nova/dragout" — start
  // with a single file path (or a list) begins a native OLE drag (CF_HDROP).
  dragout_channel_ = std::make_unique<flutter::MethodChannel<flutter::EncodableValue>>(
      flutter_controller_->engine()->messenger(), "nova/dragout",
      &flutter::StandardMethodCodec::instance());
  dragout_channel_->SetMethodCallHandler(
      [this](const flutter::MethodCall<flutter::EncodableValue>& call,
             std::unique_ptr<flutter::MethodResult<flutter::EncodableValue>> result) {
        if (call.method_name() == "start") {
          std::vector<std::wstring> files;
          const auto* args = std::get_if<flutter::EncodableList>(call.arguments());
          if (args) {
            for (const auto& a : *args)
              if (const auto* s = std::get_if<std::string>(&a))
                files.push_back(Utf8ToWide(*s));
          } else if (const auto* single = std::get_if<std::string>(call.arguments())) {
            files.push_back(Utf8ToWide(*single));
          }
          const DWORD effect = DragOutFiles(files);
          result->Success(flutter::EncodableValue(static_cast<int32_t>(effect)));
        } else {
          result->NotImplemented();
        }
      });

  flutter_controller_->engine()->SetNextFrameCallback([&]() {
    this->Show();
  });

  // Flutter can complete the first frame before the "show window" callback is
  // registered. The following call ensures a frame is pending to ensure the
  // window is shown. It is a no-op if the first frame hasn't completed yet.
  flutter_controller_->ForceRedraw();

  return true;
}

void FlutterWindow::OnDestroy() {
  TrayRemove();
  pipe_bridge_.reset();
  dnd_channel_.reset();
  tray_channel_.reset();
  dragout_channel_.reset();
  if (flutter_controller_) {
    flutter_controller_ = nullptr;
  }

  Win32Window::OnDestroy();
}

LRESULT
FlutterWindow::MessageHandler(HWND hwnd, UINT const message,
                              WPARAM const wparam,
                              LPARAM const lparam) noexcept {
  // Give Flutter, including plugins, an opportunity to handle window messages.
  if (flutter_controller_) {
    std::optional<LRESULT> result =
        flutter_controller_->HandleTopLevelWindowProc(hwnd, message, wparam,
                                                      lparam);
    if (result) {
      return *result;
    }
  }

  switch (message) {
    case WM_FONTCHANGE:
      flutter_controller_->engine()->ReloadSystemFonts();
      break;
    case NOVA_TRAY_MSG: {
      // v1.8.0: tray mouse events. lparam's LOWORD is the mouse message.
      const UINT mouse = LOWORD(lparam);
      if (mouse == WM_LBUTTONUP || mouse == WM_LBUTTONDBLCLK) {
        ::ShowWindow(GetHandle(), SW_SHOW);
        ::SetForegroundWindow(GetHandle());
      } else if (mouse == WM_RBUTTONUP || mouse == WM_CONTEXTMENU) {
        TrayShowMenu();
      }
      return 0;
    }
    case NOVA_TRAY_MENU_SHOW:
      ::ShowWindow(GetHandle(), SW_SHOW);
      ::SetForegroundWindow(GetHandle());
      return 0;
    case NOVA_TRAY_MENU_EXIT:
      // Let Dart run its exit path (didRequestAppExit) for a clean shutdown.
      TrayInvoke("onTrayExit");
      return 0;
    case WM_DROPFILES: {
      // IDM parity: drop links/files onto the window → Add dialog prefilled.
      HDROP drop = reinterpret_cast<HDROP>(wparam);
      wchar_t path[MAX_PATH];
      const UINT count = DragQueryFile(drop, 0xFFFFFFFF, nullptr, 0);
      std::vector<std::string> items;
      for (UINT i = 0; i < count && i < 64; i++) {
        if (DragQueryFile(drop, i, path, MAX_PATH) > 0) {
          int size_needed =
              WideCharToMultiByte(CP_UTF8, 0, path, -1, nullptr, 0, nullptr, nullptr);
          std::string utf8(size_needed - 1, 0);
          WideCharToMultiByte(CP_UTF8, 0, path, -1, utf8.data(), size_needed,
                              nullptr, nullptr);
          items.push_back(std::move(utf8));
        }
      }
      DragFinish(drop);
      if (!items.empty() && dnd_channel_) {
        flutter::EncodableList list;
        for (auto& s : items) list.emplace_back(flutter::EncodableValue(s));
        dnd_channel_->InvokeMethod(
            "onDrop",
            std::make_unique<flutter::EncodableValue>(flutter::EncodableValue(
                flutter::EncodableMap{{flutter::EncodableValue("items"),
                                       flutter::EncodableValue(list)}})));
      }
      return 0;
    }
  }

  return Win32Window::MessageHandler(hwnd, message, wparam, lparam);
}

// ---- v1.8.0 (IDM tray parity) --------------------------------------------

bool FlutterWindow::TrayCreate() {
  if (tray_created_) return true;
  HINSTANCE instance = GetModuleHandle(nullptr);
  tray_icon_ = {};
  tray_icon_.cbSize = sizeof(tray_icon_);
  tray_icon_.hWnd = GetHandle();
  tray_icon_.uID = 1;
  tray_icon_.uFlags = NIF_ICON | NIF_MESSAGE | NIF_TIP;
  tray_icon_.uCallbackMessage = NOVA_TRAY_MSG;
  tray_icon_.hIcon =
      LoadIcon(instance, MAKEINTRESOURCE(IDI_APP_ICON));
  if (!tray_icon_.hIcon) tray_icon_.hIcon = LoadIcon(nullptr, IDI_APPLICATION);
  wcscpy_s(tray_icon_.szTip, L"Nova Download Manager");
  tray_created_ = Shell_NotifyIconW(NIM_ADD, &tray_icon_) == TRUE;
  return tray_created_;
}

void FlutterWindow::TrayRemove() {
  if (!tray_created_) return;
  Shell_NotifyIconW(NIM_DELETE, &tray_icon_);
  tray_created_ = false;
}

void FlutterWindow::TrayShowMenu() {
  HMENU menu = CreatePopupMenu();
  AppendMenuW(menu, MF_STRING, NOVA_TRAY_MENU_SHOW, L"Open Nova");
  AppendMenuW(menu, MF_SEPARATOR, 0, nullptr);
  AppendMenuW(menu, MF_STRING, NOVA_TRAY_MENU_EXIT, L"Exit");
  POINT cursor;
  GetCursorPos(&cursor);
  // Required so the menu dismisses when clicking elsewhere.
  SetForegroundWindow(GetHandle());
  TrackPopupMenu(menu, TPM_RIGHTBUTTON, cursor.x, cursor.y, 0, GetHandle(),
                 nullptr);
  DestroyMenu(menu);
}

void FlutterWindow::TrayInvoke(const char* method) {
  if (tray_channel_) {
    tray_channel_->InvokeMethod(method, nullptr);
  }
}

// ---- v1.8.0 (IDM "drag downloaded files out of the app" parity) --------------
// Minimal OLE drag source: CF_HDROP data object + IDropSource, driven by
// DoDragDrop (runs the modal loop; the Dart call resolves when the drag ends).

namespace {

std::wstring Utf8ToWideLocal(const std::string& s) {
  if (s.empty()) return std::wstring();
  int len = MultiByteToWideChar(CP_UTF8, 0, s.c_str(), -1, nullptr, 0);
  std::wstring w(len > 0 ? len - 1 : 0, L'\0');
  if (len > 0) MultiByteToWideChar(CP_UTF8, 0, s.c_str(), -1, w.data(), len);
  return w;
}

class NovaDropSource : public IDropSource {
  LONG ref_ = 1;

 public:
  HRESULT STDMETHODCALLTYPE QueryInterface(REFIID riid, void** ppv) override {
    if (!ppv) return E_POINTER;
    if (riid == IID_IUnknown || riid == IID_IDropSource) {
      *ppv = static_cast<IDropSource*>(this);
      AddRef();
      return S_OK;
    }
    *ppv = nullptr;
    return E_NOINTERFACE;
  }
  ULONG STDMETHODCALLTYPE AddRef() override { return InterlockedIncrement(&ref_); }
  ULONG STDMETHODCALLTYPE Release() override {
    ULONG r = InterlockedDecrement(&ref_);
    if (r == 0) delete this;
    return r;
  }
  HRESULT STDMETHODCALLTYPE QueryContinueDrag(BOOL escape, DWORD keys) override {
    if (escape) return DRAGDROP_S_CANCEL;
    if (!(keys & MK_LBUTTON)) return DRAGDROP_S_DROP;
    return S_OK;
  }
  HRESULT STDMETHODCALLTYPE GiveFeedback(DWORD) override {
    return DRAGDROP_S_USEDEFAULTCURSORS;
  }
};

class NovaHdropDataObject : public IDataObject {
  LONG ref_ = 1;
  HGLOBAL block_ = nullptr;  // DROPFILES + double-null-terminated wide paths

  static HGLOBAL BuildDropFiles(const std::vector<std::wstring>& files) {
    size_t chars = 2;  // final double NUL
    for (const auto& f : files) chars += f.size() + 1;
    const size_t bytes = sizeof(DROPFILES) + chars * sizeof(wchar_t);
    HGLOBAL g = GlobalAlloc(GHND, bytes);
    if (!g) return nullptr;
    auto* df = static_cast<DROPFILES*>(GlobalLock(g));
    if (!df) {
      GlobalFree(g);
      return nullptr;
    }
    df->pFiles = sizeof(DROPFILES);
    df->fWide = TRUE;
    auto* out = reinterpret_cast<wchar_t*>(reinterpret_cast<BYTE*>(df) + sizeof(DROPFILES));
    for (const auto& f : files) {
      wcscpy_s(out, f.size() + 1, f.c_str());
      out += f.size() + 1;
    }
    *out = L'\0';  // list terminator
    GlobalUnlock(g);
    return g;
  }

 public:
  explicit NovaHdropDataObject(const std::vector<std::wstring>& files)
      : block_(BuildDropFiles(files)) {}
  ~NovaHdropDataObject() {
    if (block_) GlobalFree(block_);
  }

  HRESULT STDMETHODCALLTYPE QueryInterface(REFIID riid, void** ppv) override {
    if (!ppv) return E_POINTER;
    if (riid == IID_IUnknown || riid == IID_IDataObject) {
      *ppv = static_cast<IDataObject*>(this);
      AddRef();
      return S_OK;
    }
    *ppv = nullptr;
    return E_NOINTERFACE;
  }
  ULONG STDMETHODCALLTYPE AddRef() override { return InterlockedIncrement(&ref_); }
  ULONG STDMETHODCALLTYPE Release() override {
    ULONG r = InterlockedDecrement(&ref_);
    if (r == 0) delete this;
    return r;
  }

  HRESULT STDMETHODCALLTYPE GetData(FORMATETC* fmt, STGMEDIUM* medium) override {
    if (!fmt || !medium) return E_INVALIDARG;
    if (fmt->cfFormat != CF_HDROP || fmt->tymed != TYMED_HGLOBAL) return DV_E_FORMATETC;
    const SIZE_T bytes = GlobalSize(block_);
    medium->hGlobal = GlobalAlloc(GHND, bytes);
    if (!medium->hGlobal) return E_OUTOFMEMORY;
    memcpy(GlobalLock(medium->hGlobal), GlobalLock(block_), bytes);
    GlobalUnlock(medium->hGlobal);
    GlobalUnlock(block_);
    medium->tymed = TYMED_HGLOBAL;
    medium->pUnkForRelease = nullptr;
    return S_OK;
  }
  HRESULT STDMETHODCALLTYPE GetDataHere(FORMATETC*, STGMEDIUM*) override { return E_NOTIMPL; }
  HRESULT STDMETHODCALLTYPE QueryGetData(FORMATETC* fmt) override {
    return (fmt && fmt->cfFormat == CF_HDROP && fmt->tymed == TYMED_HGLOBAL) ? S_OK : DV_E_FORMATETC;
  }
  HRESULT STDMETHODCALLTYPE GetCanonicalFormatEtc(FORMATETC*, FORMATETC*) override { return E_NOTIMPL; }
  HRESULT STDMETHODCALLTYPE SetData(FORMATETC*, STGMEDIUM*, BOOL) override { return E_NOTIMPL; }
  HRESULT STDMETHODCALLTYPE EnumFormatEtc(DWORD direction, IEnumFORMATETC** pp) override {
    if (!pp) return E_POINTER;
    if (direction != DATADIR_GET) return E_NOTIMPL;
    static FORMATETC supported = {CF_HDROP, nullptr, DVASPECT_CONTENT, -1, TYMED_HGLOBAL};
    return SHCreateStdEnumFmtEtc(1, &supported, pp);
  }
  HRESULT STDMETHODCALLTYPE DAdvise(FORMATETC*, DWORD, IAdviseSink*, DWORD*) override { return OLE_E_ADVISENOTSUPPORTED; }
  HRESULT STDMETHODCALLTYPE DUnadvise(DWORD) override { return OLE_E_ADVISENOTSUPPORTED; }
  HRESULT STDMETHODCALLTYPE EnumDAdvise(IEnumSTATDATA**) override { return OLE_E_ADVISENOTSUPPORTED; }
};

}  // namespace

std::wstring FlutterWindow::Utf8ToWide(const std::string& s) { return Utf8ToWideLocal(s); }

bool FlutterWindow::DragOutFiles(const std::vector<std::wstring>& files) {
  if (files.empty()) return false;
  // The runner main() has OleInitialize'd the STA thread; DoDragDrop needs it.
  auto* data = new (std::nothrow) NovaHdropDataObject(files);
  auto* source = new (std::nothrow) NovaDropSource();
  if (!data || !source) {
    if (data) data->Release();
    if (source) source->Release();
    return false;
  }
  DWORD effect = DROPEFFECT_NONE;
  const HRESULT hr = DoDragDrop(data, source, DROPEFFECT_COPY, &effect);
  data->Release();
  source->Release();
  return SUCCEEDED(hr) && effect == DROPEFFECT_COPY;
}
