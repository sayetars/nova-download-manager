// Nova pipe bridge implementation. See nova_pipe_bridge.h for the contract.
#include "nova_pipe_bridge.h"

#include <flutter/binary_messenger.h>
#include <flutter/encodable_value.h>

#include <atomic>
#include <cstring>

namespace nova {
namespace {

constexpr char kChannel[] = "nova/pipe";

}  // namespace

PipeBridge::PipeBridge(flutter::BinaryMessenger* messenger)
    : channel_(messenger, kChannel, &flutter::StandardMethodCodec::GetInstance()) {
  channel_.SetMethodCallHandler([this](const auto& call, auto result) {
    HandleMethodCall(call, std::move(result));
  });
}

PipeBridge::~PipeBridge() { Stop(); }

bool PipeBridge::IsValidPipeName(const std::wstring& name) {
  // Allow only \\.\pipe\<identifier-with-dashes> — no traversal, no wildcards.
  const std::wstring prefix = L"\\\\.\\pipe\\";
  if (name.rfind(prefix, 0) != 0) return false;
  std::wstring rest = name.substr(prefix.size());
  if (rest.empty() || rest.size() > 64) return false;
  for (wchar_t c : rest) {
    if (!(iswalnum(c) || c == L'-' || c == L'_')) return false;
  }
  return true;
}

void PipeBridge::StartReader() {
  reader_ = std::thread([this]() {
    std::vector<uint8_t> buffer(64 * 1024);
    std::vector<uint8_t> pending;
    bool frame_started = false;
    size_t frame_len = 0;
    std::vector<uint8_t> frame;

    OVERLAPPED ov{};
    ov.hEvent = CreateEventW(nullptr, TRUE, FALSE, nullptr);

    while (reading_.load()) {
      HANDLE p;
      {
        std::lock_guard<std::mutex> lock(state_mutex_);
        p = pipe_;
      }
      if (p == INVALID_HANDLE_VALUE) break;

      ResetEvent(ov.hEvent);
      DWORD read = 0;
      if (!ReadFile(p, buffer.data(), (DWORD)buffer.size(), &read, &ov)) {
        if (GetLastError() != ERROR_IO_PENDING) break;
        HANDLE events[1] = {ov.hEvent};
        DWORD wait = WaitForMultipleObjects(1, events, FALSE, 250);
        if (wait != WAIT_OBJECT_0) continue;
        if (!GetOverlappedResult(p, &ov, &read, FALSE)) break;
      }
      if (read == 0) continue;

      pending.insert(pending.end(), buffer.begin(), buffer.begin() + read);
      // Extract as many complete frames as available: [4-byte LE len][payload].
      // Deliver the FULL frame (prefix included) — Framing.tryDecode on the
      // Dart side expects the length prefix in the buffer.
      while (true) {
        if (!frame_started) {
          if (pending.size() < 4) break;
          frame_len = (uint32_t)pending[0] | ((uint32_t)pending[1] << 8) |
                      ((uint32_t)pending[2] << 16) | ((uint32_t)pending[3] << 24);
          if (frame_len == 0 || frame_len > (4u * 1024 * 1024u)) {
            pending.clear();
            break;
          }
          frame.reserve(frame_len + 4);
          frame_started = true;
        }
        if (pending.size() < frame_len + 4) break;
        frame.assign(pending.begin(), pending.begin() + frame_len + 4);
        pending.erase(pending.begin(), pending.begin() + frame_len + 4);
        frame_started = false;
        DeliverFrame(frame);
      }
    }
    CloseHandle(ov.hEvent);
  });
}

void PipeBridge::DeliverFrame(const std::vector<uint8_t>& bytes) {
  // Method-channel calls from a non-platform thread are dispatched safely by
  // the engine's task runner (BinaryMessenger is thread-safe for posting).
  channel_.InvokeMethod(
      "onFrame", std::make_unique<flutter::EncodableValue>(flutter::EncodableValue(bytes)));
}

void PipeBridge::Stop() {
  reading_.store(false);
  {
    std::lock_guard<std::mutex> lock(state_mutex_);
    if (pipe_ != INVALID_HANDLE_VALUE) {
      CancelIoEx(pipe_, nullptr);
      CloseHandle(pipe_);
      pipe_ = INVALID_HANDLE_VALUE;
    }
  }
  if (reader_.joinable()) reader_.join();
}

void PipeBridge::HandleMethodCall(
    const flutter::MethodCall<flutter::EncodableValue>& call,
    std::unique_ptr<flutter::MethodResult<flutter::EncodableValue>> result) {
  const std::string& method = call.method_name();

  if (method == "connect") {
    const auto* args = std::get_if<flutter::EncodableMap>(call.arguments());
    if (!args) {
      result->Error("bad_args", "arguments must be a map");
      return;
    }
    auto pipe_name_it = args->find(flutter::EncodableValue("pipeName"));
    if (pipe_name_it == args->end()) {
      result->Error("bad_args", "pipeName required");
      return;
    }
    std::string name;
    if (const auto* s = std::get_if<std::string>(&pipe_name_it->second)) {
      name = *s;
    } else {
      result->Error("bad_args", "pipeName must be a string");
      return;
    }
    std::wstring wide(name.begin(), name.end());
    HANDLE p = CreateFileW((L"\\\\.\\pipe\\" + wide).c_str(), GENERIC_READ | GENERIC_WRITE, 0,
                          nullptr, OPEN_EXISTING, FILE_FLAG_OVERLAPPED, nullptr);
    if (p == INVALID_HANDLE_VALUE) {
      result->Success(flutter::EncodableValue(false));
      return;
    }
    DWORD mode = PIPE_READMODE_BYTE;
    SetNamedPipeHandleState(p, &mode, nullptr, nullptr);
    Stop();
    {
      std::lock_guard<std::mutex> lock(state_mutex_);
      pipe_ = p;
    }
    reading_.store(true);
    StartReader();
    result->Success(flutter::EncodableValue(true));
  } else if (method == "send") {
    const auto* args = std::get_if<flutter::EncodableMap>(call.arguments());
    if (!args) {
      result->Error("bad_args", "arguments must be a map");
      return;
    }
    auto bytes_it = args->find(flutter::EncodableValue("bytes"));
    if (bytes_it == args->end()) {
      result->Error("bad_args", "bytes required");
      return;
    }
    const auto* bytes = std::get_if<std::vector<uint8_t>>(&bytes_it->second);
    if (!bytes) {
      result->Error("bad_args", "bytes must be Uint8List");
      return;
    }
    HANDLE p;
    {
      std::lock_guard<std::mutex> lock(state_mutex_);
      p = pipe_;
    }
    if (p == INVALID_HANDLE_VALUE) {
      result->Error("not_connected", "pipe is not open");
      return;
    }
    std::lock_guard<std::mutex> lock(write_mutex_);
    OVERLAPPED ov{};
    ov.hEvent = CreateEventW(nullptr, TRUE, FALSE, nullptr);
    DWORD written = 0;
    BOOL ok = WriteFile(p, bytes->data(), (DWORD)bytes->size(), &written, &ov);
    if (!ok && GetLastError() == ERROR_IO_PENDING) {
      if (!GetOverlappedResult(p, &ov, &written, TRUE)) ok = FALSE;
    }
    CloseHandle(ov.hEvent);
    if (ok && written == bytes->size()) {
      result->Success();
    } else {
      result->Error("write_failed", "pipe write failed");
    }
  } else if (method == "disconnect") {
    Stop();
    result->Success();
  } else {
    result->NotImplemented();
  }
}

}  // namespace nova
