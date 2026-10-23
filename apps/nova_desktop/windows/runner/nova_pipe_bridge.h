// Nova pipe bridge — connects the Dart UI to the Nova engine's named pipe.
//
// Method channel "nova/pipe":
//   connect(pipeName, token) -> bool
//   send(bytes: Uint8List)    -> void
//   disconnect()              -> void
//   (events)  "onFrame" with Uint8List — invoked from the reader thread
//              (throttled through the platform thread by the engine messenger)
//
// The pipe name is validated against a strict allow-list pattern to prevent
// any path/pipe confusion. Only one connection is kept at a time.
#ifndef RUNNER_NOVA_PIPE_BRIDGE_H_
#define RUNNER_NOVA_PIPE_BRIDGE_H_

#include <flutter/basic_message_channel.h>
#include <flutter/method_channel.h>
#include <flutter/standard_method_codec.h>
#include <windows.h>

#include <cstdint>
#include <memory>
#include <mutex>
#include <string>
#include <thread>
#include <vector>

namespace nova {

class PipeBridge {
 public:
  explicit PipeBridge(flutter::BinaryMessenger* messenger);
  ~PipeBridge();

  void HandleMethodCall(const flutter::MethodCall<flutter::EncodableValue>& call,
                        std::unique_ptr<flutter::MethodResult<flutter::EncodableValue>> result);

 private:
  void StartReader();
  void Stop();
  void DeliverFrame(const std::vector<uint8_t>& bytes);
  static bool IsValidPipeName(const std::wstring& name);

  flutter::MethodChannel<flutter::EncodableValue> channel_;
  HANDLE pipe_ = INVALID_HANDLE_VALUE;
  std::thread reader_;
  std::mutex write_mutex_;
  std::atomic<bool> reading_{false};
  std::mutex state_mutex_;
};

}  // namespace nova

#endif  // RUNNER_NOVA_PIPE_BRIDGE_H_
