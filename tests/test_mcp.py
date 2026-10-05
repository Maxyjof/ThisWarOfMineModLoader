import json
import pathlib
import subprocess
import tempfile
import threading
import time

ROOT = pathlib.Path(__file__).resolve().parents[1]
SERVER = ROOT / 'MaxyModLoader.Cli/bin/Release/net10.0/MaxyModLoader.dll'


def serve_files(directory, stop, received):
    """
    <summary>
    用独立文件端模拟游戏线程验证真实传输和请求关联
    </summary>
    """
    #测试替身只验证协议真实游戏效果另行记录
    request = directory / 'request.txt'
    while not stop.is_set():
        if not request.exists():
            time.sleep(0.01)
            continue
        lines = request.read_text(encoding='utf-8').splitlines()
        request.unlink()
        assert lines[0] == 'MML1' and len(lines[1]) == 32
        argument = bytes.fromhex(lines[3]).decode('utf-8') if len(lines) > 3 else ''
        received.append((lines[2], argument))
        reply = {'id': lines[1], 'ok': True, 'result': {'argument': argument}}
        temporary = directory / 'response.tmp'
        temporary.write_text(json.dumps(reply, ensure_ascii=False), encoding='utf-8')
        temporary.replace(directory / 'response.json')


def main():
    """
    <summary>
    验证MCP握手工具发现参数错误隔离和Unicode文件往返
    </summary>
    """
    with tempfile.TemporaryDirectory(prefix='maxy-mcp-test-') as game:
        #使用临时目录确保不接触玩家游戏和存档
        directory = pathlib.Path(game) / 'MaxyModLoader/mcp'
        directory.mkdir(parents=True)
        stop, received = threading.Event(), []
        worker = threading.Thread(target=serve_files, args=(directory, stop, received), daemon=True)
        worker.start()
        requests = [
            {'jsonrpc': '2.0', 'id': 1, 'method': 'tools/list'},
            {'jsonrpc': '2.0', 'id': 2, 'method': 'initialize', 'params': {'protocolVersion': '2025-06-18'}},
            {'jsonrpc': '2.0', 'method': 'notifications/initialized'},
            {'jsonrpc': '2.0', 'id': 3, 'method': 'tools/list'},
            {'jsonrpc': '2.0', 'id': 4, 'method': 'tools/call', 'params': {'name': 'ui_click', 'arguments': {'name': '中文按钮'}}},
            {'jsonrpc': '2.0', 'id': 5, 'method': 'tools/call', 'params': {'name': 'set_pause', 'arguments': {'paused': 'false'}}},
            {'jsonrpc': '2.0', 'id': 6, 'method': 'tools/call', 'params': {'name': 'ui_click', 'arguments': {'name': 'x', 'unknown': True}}},
            {'jsonrpc': '2.0', 'id': 7, 'method': 'tools/call', 'params': {'name': 'missing'}},
            {'jsonrpc': '2.0', 'id': 8, 'method': 'tools/call', 'params': {'name': 'game_state', 'arguments': {}}},
            {'jsonrpc': '2.0', 'id': 9, 'method': 'ping'},
        ]
        #错误消息之后仍需正常响应证明服务不会被单个请求终止
        payload = '\n'.join(json.dumps(request, ensure_ascii=False) for request in requests)
        payload += '\ninvalid-json\n[]\n' + json.dumps({'jsonrpc': '2.0', 'id': 10, 'method': 'ping'}) + '\n'
        try:
            result = subprocess.run(['dotnet', str(SERVER), 'mcp', '--game', game], input=payload, text=True,
                                    encoding='utf-8', capture_output=True, timeout=20, check=True)
        finally:
            stop.set()
            worker.join(timeout=2)
        replies = [json.loads(line) for line in result.stdout.splitlines()]
        by_id = {reply['id']: reply for reply in replies if reply['id'] is not None}
        assert by_id[1]['error']['code'] == -32002
        assert by_id[2]['result']['serverInfo']['name'] == 'MaxyModLoader'
        tools = by_id[3]['result']['tools']
        assert len(tools) == 19 and len({tool['name'] for tool in tools}) == 19
        assert next(tool for tool in tools if tool['name'] == 'rule_list')['annotations']['readOnlyHint']
        assert next(tool for tool in tools if tool['name'] == 'item_config')['annotations']['readOnlyHint']
        assert next(tool for tool in tools if tool['name'] == 'settings_state')['annotations']['readOnlyHint']
        display = next(tool for tool in tools if tool['name'] == 'display_mode')
        assert display['inputSchema']['required'] == ['mode']
        assert display['annotations']['readOnlyHint'] is False
        assert not by_id[4]['result']['isError']
        assert by_id[5]['result']['isError'] and by_id[6]['result']['isError']
        assert by_id[7]['error']['code'] == -32602
        assert not by_id[8]['result']['isError'] and by_id[10]['result'] == {}
        assert received == [('ui_click', '中文按钮'), ('game_state', '')]
        assert {reply['error']['code'] for reply in replies if reply['id'] is None} == {-32700, -32600}
        assert not result.stderr
        print('通过：MCP握手、十九项工具、规则查询、错误隔离和Unicode传输')
        #独立客户端并发时必须依次取得锁且各自收到关联响应
        stop.clear()
        received.clear()
        worker = threading.Thread(target=serve_files, args=(directory, stop, received), daemon=True)
        worker.start()
        clients = [subprocess.Popen(['dotnet', str(SERVER), 'rpc', game, 'ui_click', name],
                    text=True, encoding='utf-8', stdout=subprocess.PIPE, stderr=subprocess.PIPE) for name in ['并发甲', '并发乙']]
        try:
            replies = [client.communicate(timeout=20) for client in clients]
        finally:
            stop.set()
            worker.join(timeout=2)
        for client, (output, error), name in zip(clients, replies, ['并发甲', '并发乙']):
            assert client.returncode == 0 and not error
            assert json.loads(output)['result']['argument'] == name
        assert sorted(received) == [('ui_click', '并发乙'), ('ui_click', '并发甲')]
        print('通过：独立客户端并发传输不覆盖请求')


if __name__ == '__main__':
    main()
