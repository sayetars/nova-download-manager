import base64, json, sys
sys.path.insert(0, '/home/z/my-project/scripts')
from zai_web_dev_sdk import ZaiClient  # noqa

def main():
    with open('/home/z/my-project/download/nova-mini-window-crop.png', 'rb') as f:
        b64 = base64.b64encode(f.read()).decode()
    client = ZaiClient()
    resp = client.chat.completions.create(
        model='glm-4.5v',
        messages=[{
            'role': 'user',
            'content': [
                {'type': 'image_url', 'image_url': {'url': f'data:image/png;base64,{b64}'}},
                {'type': 'text', 'text': 'This is a screenshot of a compact download-manager progress window (dark theme, ~350x470 px). Answer strictly: 1) Does it show a compact panel (NOT a large full app with sidebar)? 2) Is there a speed indicator at top (may be 0 B/s)? 3) Are there pause/resume/close buttons in the header? 4) Is there an empty-state or download rows? Reply as JSON: {"compact": true/false, "speed_visible": true/false, "header_buttons": true/false, "content": "empty_state"|"rows"|"other", "verdict": "PASS"|"FAIL", "note": "..."}'},
            ],
        }],
    )
    print(resp.choices[0].message.content)

if __name__ == '__main__':
    main()
