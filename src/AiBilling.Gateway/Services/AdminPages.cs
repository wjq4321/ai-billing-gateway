namespace AiBilling.Gateway.Services;

/// <summary>管理后台页面（内嵌 HTML，单文件部署，无需静态资源服务器）。</summary>
public static class AdminPages
{
    public const string Root = """
        <!DOCTYPE html>
        <html lang="zh-CN"><head><meta charset="utf-8">
        <title>AI 计费网关</title>
        <style>
          body{font-family:system-ui,-apple-system,"Segoe UI","Microsoft YaHei",sans-serif;
               max-width:760px;margin:60px auto;padding:0 20px;color:#1a1a2e;line-height:1.7}
          h1{border-bottom:3px solid #4361ee;padding-bottom:12px}
          code{background:#f0f2f5;padding:2px 7px;border-radius:4px;font-size:.92em}
          pre{background:#1a1a2e;color:#e8e8f0;padding:16px;border-radius:8px;overflow-x:auto}
          header{display:flex;justify-content:space-between;align-items:baseline}
          .badge{background:#4361ee;color:#fff;padding:3px 10px;border-radius:12px;font-size:.75em}
          a.btn{display:inline-block;background:#4361ee;color:#fff;padding:10px 20px;border-radius:6px;
                text-decoration:none;margin-top:12px}
        </style></head><body>
        <header><h1>AI 计费网关 <span class="badge">Ollama</span></h1></header>
        <p>这是一个架设在 Ollama 前面的反向代理网关，为本地模型提供 <strong>API Key 强制认证</strong>
           与 <strong>Token 计量计费</strong>。</p>
        <h2>快速开始</h2>
        <p>把原来指向 <code>http://localhost:11434</code> 的请求改到本网关，并带上 Key：</p>
        <pre>curl http://localhost:8080/api/chat \
          -H "Authorization: Bearer sk-你的密钥" \
          -H "Content-Type: application/json" \
          -d '{"model":"gemma3:270m","messages":[{"role":"user","content":"你好"}],"stream":false}'</pre>
        <p>响应头会带回本次计费结果：<code>X-Billing-Tokens-Prompt</code>、
           <code>X-Billing-Tokens-Completion</code>、<code>X-Billing-Cost</code>。</p>
        <a class="btn" href="/admin/dashboard">进入管理后台</a>
        </body></html>
        """;

    public const string Login = """
        <!DOCTYPE html>
        <html lang="zh-CN"><head><meta charset="utf-8">
        <meta name="viewport" content="width=device-width,initial-scale=1">
        <title>登录 · AI 计费网关</title>
        <style>
          *{box-sizing:border-box}
          body{font-family:system-ui,-apple-system,"Segoe UI","Microsoft YaHei",sans-serif;
               background:linear-gradient(135deg,#4361ee,#3a0ca3);min-height:100vh;
               display:flex;align-items:center;justify-content:center;margin:0;padding:20px}
          .card{background:#fff;padding:40px;border-radius:14px;width:100%;max-width:380px;
                box-shadow:0 20px 50px rgba(0,0,0,.25)}
          h1{margin:0 0 6px;font-size:1.5em;color:#1a1a2e}
          p.sub{margin:0 0 26px;color:#666;font-size:.9em}
          label{display:block;font-size:.85em;color:#444;margin-bottom:6px;font-weight:500}
          input{width:100%;padding:11px 13px;border:1.5px solid #ddd;border-radius:7px;
                font-size:.95em;margin-bottom:18px;outline:none;transition:.2s}
          input:focus{border-color:#4361ee;box-shadow:0 0 0 3px rgba(67,97,238,.12)}
          button{width:100%;padding:12px;background:#4361ee;color:#fff;border:0;border-radius:7px;
                 font-size:1em;font-weight:600;cursor:pointer;transition:.2s}
          button:hover{background:#324bc7}
          .err{background:#fee;color:#c00;padding:11px;border-radius:7px;font-size:.88em;
               margin-bottom:16px;display:none}
        </style></head><body>
        <div class="card">
          <h1>AI 计费网关</h1>
          <p class="sub">请登录管理后台</p>
          <div class="err" id="err"></div>
          <form id="f">
            <label for="u">用户名</label>
            <input id="u" value="admin" autocomplete="username" required>
            <label for="p">密码</label>
            <input id="p" type="password" autocomplete="current-password" required>
            <button type="submit">登 录</button>
          </form>
        </div>
        <script>
        const f=document.getElementById('f'),err=document.getElementById('err');
        f.addEventListener('submit',async e=>{
          e.preventDefault();err.style.display='none';
          try{
            const r=await fetch('/api/admin/login',{method:'POST',headers:{'Content-Type':'application/json'},
              body:JSON.stringify({username:document.getElementById('u').value,password:document.getElementById('p').value})});
            const j=await r.json();
            if(r.ok){location.href='/admin/dashboard';}
            else{err.textContent=(j.error&&j.error.message)||'登录失败';err.style.display='block';}
          }catch(ex){err.textContent='网络错误：'+ex.message;err.style.display='block';}
        });
        </script></body></html>
        """;

    public const string Dashboard = """
        <!DOCTYPE html>
        <html lang="zh-CN"><head><meta charset="utf-8">
        <meta name="viewport" content="width=device-width,initial-scale=1">
        <title>管理后台 · AI 计费网关</title>
        <style>
          *{box-sizing:border-box}
          body{font-family:system-ui,-apple-system,"Segoe UI","Microsoft YaHei",sans-serif;
               margin:0;background:#f5f7fb;color:#1a1a2e;font-size:14px}
          header{background:#1a1a2e;color:#fff;padding:14px 26px;display:flex;
                 justify-content:space-between;align-items:center;position:sticky;top:0;z-index:50}
          header h1{margin:0;font-size:1.1em;font-weight:600}
          header .right{display:flex;gap:14px;align-items:center;font-size:.85em}
          header a{color:#9db4ff;text-decoration:none;cursor:pointer}
          .wrap{max-width:1240px;margin:24px auto;padding:0 20px}
          .cards{display:grid;grid-template-columns:repeat(auto-fit,minmax(180px,1fr));gap:14px;margin-bottom:24px}
          .card{background:#fff;border-radius:11px;padding:18px;box-shadow:0 1px 4px rgba(0,0,0,.07)}
          .card .lbl{color:#777;font-size:.8em;margin-bottom:7px;text-transform:uppercase;letter-spacing:.4px}
          .card .val{font-size:1.75em;font-weight:700;color:#4361ee}
          .card .sub{font-size:.78em;color:#999;margin-top:3px}
          .tabs{display:flex;gap:5px;border-bottom:2px solid #e2e6f0;margin-bottom:20px;flex-wrap:wrap}
          .tab{padding:10px 18px;cursor:pointer;border:0;background:none;font-size:.93em;
               color:#666;border-bottom:2.5px solid transparent;margin-bottom:-2px;font-weight:500}
          .tab.active{color:#4361ee;border-bottom-color:#4361ee}
          .panel{display:none}.panel.active{display:block}
          .box{background:#fff;border-radius:11px;padding:20px;margin-bottom:18px;box-shadow:0 1px 4px rgba(0,0,0,.07)}
          .box h2{margin:0 0 16px;font-size:1.02em;display:flex;justify-content:space-between;align-items:center}
          table{width:100%;border-collapse:collapse;font-size:.87em}
          th{text-align:left;padding:9px 10px;background:#f8f9fc;color:#555;font-weight:600;
             border-bottom:2px solid #e8ecf4;white-space:nowrap;font-size:.92em}
          td{padding:9px 10px;border-bottom:1px solid #f0f2f7;vertical-align:middle}
          tr:hover td{background:#fafbfe}
          button{cursor:pointer;border:0;border-radius:6px;padding:7px 13px;font-size:.86em;
                 font-weight:500;transition:.15s}
          .b-pri{background:#4361ee;color:#fff}.b-pri:hover{background:#324bc7}
          .b-sec{background:#eef1f8;color:#33415c}.b-sec:hover{background:#e0e5f2}
          .b-dan{background:#ffeaea;color:#c0392b}.b-dan:hover{background:#ffd9d9}
          .b-suc{background:#e7f8ee;color:#1a7f4b}.b-suc:hover{background:#d4f0e0}
          input,select{padding:8px 10px;border:1.5px solid #dde2ec;border-radius:6px;
                       font-size:.9em;font-family:inherit;outline:none;width:100%;transition:.15s}
          input:focus,select:focus{border-color:#4361ee;box-shadow:0 0 0 3px rgba(67,97,238,.1)}
          .grid{display:grid;grid-template-columns:repeat(auto-fit,minmax(190px,1fr));gap:14px}
          .fld label{display:block;font-size:.8em;color:#666;margin-bottom:5px;font-weight:500}
          .hint{font-size:.78em;color:#999;margin-top:4px}
          .pill{display:inline-block;padding:3px 9px;border-radius:11px;font-size:.76em;font-weight:600}
          .pill.ok{background:#e7f8ee;color:#1a7f4b}
          .pill.off{background:#f0f0f0;color:#777}
          .pill.exp{background:#fff4e5;color:#b26a00}
          .pill.err{background:#ffeaea;color:#c0392b}
          .key{font-family:ui-monospace,Consolas,monospace;font-size:.86em;background:#f4f6fb;
               padding:3px 7px;border-radius:5px}
          .modal{position:fixed;inset:0;background:rgba(20,24,40,.55);display:none;
                 align-items:center;justify-content:center;z-index:200;padding:20px}
          .modal.show{display:flex}
          .modal .inner{background:#fff;border-radius:13px;padding:26px;max-width:640px;width:100%;
                        max-height:88vh;overflow-y:auto}
          .modal h3{margin:0 0 18px;font-size:1.08em}
          .warnbox{background:#fff8e1;border-left:4px solid #f0ad4e;padding:13px;border-radius:6px;
                   margin:14px 0;font-size:.87em;line-height:1.6}
          .plainkey{font-family:ui-monospace,Consolas,monospace;background:#1a1a2e;color:#7ee787;
                    padding:13px;border-radius:7px;word-break:break-all;font-size:.87em;user-select:all}
          .toast{position:fixed;bottom:26px;right:26px;background:#1a1a2e;color:#fff;
                 padding:13px 20px;border-radius:9px;font-size:.89em;opacity:0;
                 transition:.3s;pointer-events:none;z-index:300;max-width:400px}
          .toast.show{opacity:1}
          .bar{height:6px;background:#eef1f8;border-radius:3px;overflow:hidden;min-width:70px}
          .bar>div{height:100%;background:#4361ee;border-radius:3px}
          .bar.warn>div{background:#f0ad4e}.bar.danger>div{background:#e74c3c}
          .muted{color:#999;font-size:.85em}
          .right{text-align:right}
          .empty{text-align:center;color:#999;padding:30px;font-size:.9em}
          .chart{display:flex;align-items:flex-end;gap:5px;height:150px;padding:14px 6px;
                 border-bottom:2px solid #e8ecf4;margin-bottom:8px;overflow-x:auto}
          .chart .col{flex:1;min-width:22px;background:linear-gradient(#4361ee,#7c93ff);
                      border-radius:4px 4px 0 0;position:relative;transition:.2s;min-height:3px}
          .chart .col:hover{filter:brightness(1.15)}
          .chart .col span{position:absolute;bottom:-22px;left:50%;transform:translateX(-50%);
                           font-size:.68em;color:#888;white-space:nowrap}
        </style></head><body>

        <header>
          <h1>⚡ AI 计费网关</h1>
          <div class="right">
            <span id="upstreamState" class="muted">检测上游…</span>
            <a onclick="logout()">退出登录</a>
          </div>
        </header>

        <div class="wrap">
          <div class="cards">
            <div class="card"><div class="lbl">API Key 总数</div><div class="val" id="s-keys">–</div>
              <div class="sub" id="s-keys-sub">–</div></div>
            <div class="card"><div class="lbl">累计 Token</div><div class="val" id="s-tokens">–</div>
              <div class="sub" id="s-tokens-sub">输入 + 输出</div></div>
            <div class="card"><div class="lbl">累计费用</div><div class="val" id="s-cost">–</div>
              <div class="sub" id="s-cost-sub">–</div></div>
            <div class="card"><div class="lbl">累计请求</div><div class="val" id="s-req">–</div>
              <div class="sub" id="s-req-sub">–</div></div>
            <div class="card"><div class="lbl">今日</div><div class="val" id="s-today">–</div>
              <div class="sub" id="s-today-sub">–</div></div>
          </div>

          <div class="tabs">
            <button class="tab active" data-t="keys">API Keys</button>
            <button class="tab" data-t="prices">模型定价</button>
            <button class="tab" data-t="usage">用量明细</button>
            <button class="tab" data-t="stats">统计报表</button>
            <button class="tab" data-t="calib">计费校准</button>
            <button class="tab" data-t="account">账号安全</button>
            <button class="tab" data-t="settings">系统设置</button>
          </div>

          <!-- ==================== Keys ==================== -->
          <div class="panel active" id="p-keys">
            <div class="box">
              <h2>API Keys
                <button class="b-pri" onclick="openCreate()">+ 新建 Key</button>
              </h2>
              <div id="keysTable"><div class="empty">加载中…</div></div>
            </div>
          </div>

          <!-- ==================== 定价 ==================== -->
          <div class="panel" id="p-prices">
            <div class="box">
              <h2>模型定价规则 <span class="muted" style="font-weight:400;font-size:.85em">
                精确匹配优先于通配；未匹配到的模型按兜底规则（默认免费）</span></h2>
              <div class="grid" style="margin-bottom:16px">
                <div class="fld"><label>模型名（支持 * 通配，如 qwen2.5:* ）</label>
                  <input id="pr-model" placeholder="gemma3:270m 或 *"></div>
                <div class="fld"><label>计价模式</label>
                  <select id="pr-mode">
                    <option value="PerToken">按 Token 计费</option>
                    <option value="PerRequest">按请求次数计费</option>
                  </select></div>
                <div class="fld"><label>输入价 / 千 token</label>
                  <input id="pr-in" type="number" step="0.000001" value="0"></div>
                <div class="fld"><label>输出价 / 千 token</label>
                  <input id="pr-out" type="number" step="0.000001" value="0"></div>
                <div class="fld"><label>单次价（按次模式用）</label>
                  <input id="pr-req" type="number" step="0.000001" value="0"></div>
                <div class="fld"><label>&nbsp;</label>
                  <button class="b-pri" style="width:100%" onclick="savePrice()">保存定价</button></div>
              </div>
              <div id="pricesTable"><div class="empty">加载中…</div></div>
            </div>
          </div>

          <!-- ==================== 用量 ==================== -->
          <div class="panel" id="p-usage">
            <div class="box">
              <h2>用量明细
                <span>
                  <select id="u-key" style="width:auto;display:inline-block" onchange="loadUsage()"></select>
                  <select id="u-limit" style="width:auto;display:inline-block" onchange="loadUsage()">
                    <option value="100">最近 100 条</option>
                    <option value="500">最近 500 条</option>
                    <option value="2000">最近 2000 条</option>
                  </select>
                  <button class="b-sec" onclick="loadUsage()">刷新</button>
                </span>
              </h2>
              <div id="usageTable"><div class="empty">加载中…</div></div>
            </div>
          </div>

          <!-- ==================== 统计 ==================== -->
          <div class="panel" id="p-stats">
            <div class="box">
              <h2>近 14 天 Token 消耗趋势</h2>
              <div id="chart"><div class="empty">加载中…</div></div>
            </div>
            <div class="box">
              <h2>各模型用量分布</h2>
              <div id="modelTable"><div class="empty">加载中…</div></div>
            </div>
          </div>

          <!-- ==================== 校准 ==================== -->
          <div class="panel" id="p-calib">
            <div class="box">
              <h2>计费精度校准 <button class="b-sec" onclick="loadCalib()">重新计算</button></h2>
              <p class="muted" style="line-height:1.8">
                实时流模式下网关用文本长度估算 token。这里对比「估算值」与 Ollama 实际返回的
                <code>eval_count</code>，用于判断计费是否偏差。比值 &gt; 1 表示实际消耗比记账的多（在亏），
                &lt; 1 表示记账偏多（在赚）。</p>
              <div class="cards" id="calibCards" style="margin-top:18px"></div>
              <div id="calibHint" style="margin-top:14px"></div>
            </div>
          </div>

          <!-- ==================== 账号安全 ==================== -->
          <div class="panel" id="p-account">
            <div class="box" style="max-width:620px">
              <h2>修改管理端账号</h2>
              <p class="muted" style="margin-top:-6px;line-height:1.8" id="acc-hint">加载中…</p>

              <div style="background:#f8f9fc;border-radius:8px;padding:14px;margin:18px 0">
                <div class="muted" style="font-size:.85em">当前用户名</div>
                <div style="font-size:1.15em;font-weight:600;color:#4361ee" id="acc-current">–</div>
              </div>

              <div class="grid">
                <div class="fld"><label>当前密码 <span style="color:#c0392b">*</span></label>
                  <input id="acc-curpass" type="password" placeholder="验证身份用，必填"
                         autocomplete="current-password"></div>
              </div>

              <hr style="border:0;border-top:1px solid #eef1f8;margin:22px 0">
              <p class="muted" style="margin:0 0 12px;font-size:.86em">
                下面两项<strong>只填你想改的</strong>；留空则保持原样。</p>

              <div class="grid">
                <div class="fld"><label>新用户名</label>
                  <input id="acc-newuser" placeholder="留空=不修改" autocomplete="username"></div>
                <div class="fld"><label>新密码（至少 8 位）</label>
                  <input id="acc-newpass" type="password" placeholder="留空=不修改"
                         autocomplete="new-password"></div>
                <div class="fld"><label>确认新密码</label>
                  <input id="acc-confirm" type="password" placeholder="再次输入新密码"
                         autocomplete="new-password"></div>
              </div>

              <div id="acc-msg" style="display:none;margin-top:16px"></div>

              <div style="display:flex;gap:10px;justify-content:flex-end;margin-top:22px">
                <button class="b-sec" onclick="clearAccountForm()">清空</button>
                <button class="b-pri" onclick="saveAccount()">保存并重新登录</button>
              </div>
            </div>
          </div>

          <!-- ==================== 设置 ==================== -->
          <div class="panel" id="p-settings">
            <div class="box">
              <h2>运行配置</h2>
              <div id="settingsView"><div class="empty">加载中…</div></div>
            </div>
            <div class="box">
              <h2>接入示例</h2>
              <pre style="background:#1a1a2e;color:#e8e8f0;padding:15px;border-radius:8px;overflow-x:auto;font-size:.85em">curl http://localhost:8080/api/chat \
          -H "Authorization: Bearer sk-你的密钥" \
          -H "Content-Type: application/json" \
          -d '{"model":"gemma3:270m","messages":[{"role":"user","content":"你好"}],"stream":false}'</pre>
              <p class="muted">响应头带回计费结果：<code>X-Billing-Tokens-Prompt</code>、
                 <code>X-Billing-Tokens-Completion</code>、<code>X-Billing-Cost</code></p>
            </div>
          </div>
        </div>

        <!-- ==================== 新建 Key 弹窗 ==================== -->
        <div class="modal" id="m-create">
          <div class="inner">
            <h3>新建 API Key</h3>
            <div class="grid">
              <div class="fld"><label>名称 / 备注</label><input id="c-name" placeholder="张三的账号"></div>
              <div class="fld"><label>Token 总配额（留空=不限）</label>
                <input id="c-qtok" type="number" placeholder="如 1000000"></div>
              <div class="fld"><label>金额上限（留空=不限）</label>
                <input id="c-qcost" type="number" step="0.01" placeholder="如 100"></div>
              <div class="fld"><label>请求数上限（留空=不限）</label>
                <input id="c-qreq" type="number" placeholder="如 10000"></div>
              <div class="fld"><label>限速 RPM（留空=不限）</label>
                <input id="c-rpm" type="number" placeholder="如 60"></div>
              <div class="fld"><label>输出速度上限（token/秒，留空=不限）</label>
                <input id="c-maxout" type="number" step="0.1" placeholder="如 20"></div>
              <div class="fld"><label>每分钟 token 上限（留空=不限）</label>
                <input id="c-tpm" type="number" placeholder="如 50000"></div>
              <div class="fld"><label>每天 token 上限（留空=不限）</label>
                <input id="c-tpd" type="number" placeholder="如 2000000"></div>
              <div class="fld"><label>过期时间（留空=永不过期）</label>
                <input id="c-exp" type="datetime-local"></div>
              <div class="fld"><label>允许的模型（逗号分隔，留空=全部）</label>
                <input id="c-models" placeholder="gemma3:270m, qwen2.5:*"></div>
            </div>
            <details style="margin-top:16px">
              <summary style="cursor:pointer;color:#4361ee;font-weight:500">高级：为该 Key 单独定价（覆盖模型默认价）</summary>
              <div class="grid" style="margin-top:13px">
                <div class="fld"><label>输入价 / 千 token</label>
                  <input id="c-oip" type="number" step="0.000001" placeholder="留空=用模型默认价"></div>
                <div class="fld"><label>输出价 / 千 token</label>
                  <input id="c-oop" type="number" step="0.000001" placeholder="留空=用模型默认价"></div>
                <div class="fld"><label>单次价</label>
                  <input id="c-opr" type="number" step="0.000001" placeholder="留空=用模型默认价"></div>
              </div>
            </details>
            <div style="display:flex;gap:10px;justify-content:flex-end;margin-top:22px">
              <button class="b-sec" onclick="closeCreate()">取消</button>
              <button class="b-pri" onclick="createKey()">创建</button>
            </div>
          </div>
        </div>

        <!-- ==================== 编辑 Key 弹窗 ==================== -->
        <div class="modal" id="m-edit">
          <div class="inner">
            <h3>编辑 API Key <span id="e-title" class="muted" style="font-weight:400;font-size:.85em"></span></h3>

            <div class="warnbox" style="background:#eef3ff;border-left-color:#4361ee">
              留空表示<strong>不限制</strong>。修改后<strong>立即生效</strong>，无需重启网关。
              已累计的用量不会被清零。
            </div>

            <div class="grid">
              <div class="fld"><label>名称 / 备注</label><input id="e-name"></div>
              <div class="fld"><label>状态</label>
                <select id="e-status">
                  <option value="Active">启用</option>
                  <option value="Disabled">禁用</option>
                </select></div>
              <div class="fld"><label>Token 总配额</label>
                <input id="e-qtok" type="number" placeholder="留空=不限"></div>
              <div class="fld"><label>金额上限</label>
                <input id="e-qcost" type="number" step="0.01" placeholder="留空=不限"></div>
              <div class="fld"><label>请求数上限</label>
                <input id="e-qreq" type="number" placeholder="留空=不限"></div>
              <div class="fld"><label>限速 RPM</label>
                <input id="e-rpm" type="number" placeholder="留空=不限"></div>
              <div class="fld"><label>输出速度上限（token/秒）</label>
                <input id="e-maxout" type="number" step="0.1" placeholder="留空=不限"></div>
              <div class="fld"><label>每分钟 token 上限</label>
                <input id="e-tpm" type="number" placeholder="留空=不限"></div>
              <div class="fld"><label>每天 token 上限</label>
                <input id="e-tpd" type="number" placeholder="留空=不限"></div>
              <div class="fld"><label>过期时间</label>
                <input id="e-exp" type="datetime-local"></div>
              <div class="fld"><label>允许的模型（逗号分隔）</label>
                <input id="e-models" placeholder="留空=全部；支持 gemma* 通配"></div>
            </div>

            <details style="margin-top:16px">
              <summary style="cursor:pointer;color:#4361ee;font-weight:500">高级：该 Key 的专属定价（覆盖模型默认价）</summary>
              <div class="grid" style="margin-top:13px">
                <div class="fld"><label>输入价 / 千 token</label>
                  <input id="e-oip" type="number" step="0.000001" placeholder="留空=用模型默认价"></div>
                <div class="fld"><label>输出价 / 千 token</label>
                  <input id="e-oop" type="number" step="0.000001" placeholder="留空=用模型默认价"></div>
                <div class="fld"><label>单次价</label>
                  <input id="e-opr" type="number" step="0.000001" placeholder="留空=用模型默认价"></div>
              </div>
            </details>

            <div id="e-usage" class="muted" style="margin-top:16px;font-size:.85em"></div>

            <div style="display:flex;gap:10px;justify-content:flex-end;margin-top:22px">
              <button class="b-sec" onclick="closeEdit()">取消</button>
              <button class="b-pri" onclick="saveEdit()">保存修改</button>
            </div>
          </div>
        </div>

        <!-- ==================== 显示新 Key 弹窗 ==================== -->
        <div class="modal" id="m-show">
          <div class="inner">
            <h3>✅ API Key 创建成功</h3>
            <div class="warnbox">⚠️ <strong>请立即复制保存。</strong>
              网关只保存 Key 的哈希值，关闭本窗口后将<strong>无法再次查看</strong>明文。</div>
            <div class="plainkey" id="showPlain"></div>
            <div style="display:flex;gap:10px;justify-content:flex-end;margin-top:20px">
              <button class="b-pri" onclick="copyKey()">复制 Key</button>
              <button class="b-sec" onclick="document.getElementById('m-show').classList.remove('show')">我已保存</button>
            </div>
          </div>
        </div>

        <div class="toast" id="toast"></div>

        <script>
        // ---------------------------------------------------------------- 基础
        const $ = s => document.querySelector(s);
        const fmt = n => (n === null || n === undefined) ? '不限' : Number(n).toLocaleString('zh-CN');
        const money = v => Number(v || 0).toFixed(4);
        const esc = s => String(s ?? '').replace(/[&<>"']/g, c =>
          ({'&':'&amp;','<':'&lt;','>':'&gt;','"':'&quot;',"'":'&#39;'}[c]));

        function toast(msg, isErr) {
          const t = $('#toast');
          t.textContent = msg;
          t.style.background = isErr ? '#c0392b' : '#1a1a2e';
          t.classList.add('show');
          clearTimeout(t._h);
          t._h = setTimeout(() => t.classList.remove('show'), 2800);
        }

        async function api(path, opts) {
          const r = await fetch('/api/admin' + path, Object.assign({
            headers: { 'Content-Type': 'application/json' }
          }, opts || {}));
          if (r.status === 401) { location.href = '/admin/login'; throw new Error('未登录'); }
          const text = await r.text();
          let j = null;
          try { j = text ? JSON.parse(text) : null; } catch (e) { j = text; }
          if (!r.ok) throw new Error((j && j.error && j.error.message) || ('HTTP ' + r.status));
          return j;
        }

        function logout() { fetch('/api/admin/logout', { method: 'POST' }).then(() => location.href = '/admin/login'); }

        // ---------------------------------------------------------------- 标签页
        document.querySelectorAll('.tab').forEach(b => b.addEventListener('click', () => {
          document.querySelectorAll('.tab').forEach(x => x.classList.remove('active'));
          document.querySelectorAll('.panel').forEach(x => x.classList.remove('active'));
          b.classList.add('active');
          $('#p-' + b.dataset.t).classList.add('active');
          if (b.dataset.t === 'usage') loadUsage();
          if (b.dataset.t === 'stats') { loadChart(); loadByModel(); }
          if (b.dataset.t === 'calib') loadCalib();
          if (b.dataset.t === 'account') loadAccount();
          if (b.dataset.t === 'prices') loadPrices();
          if (b.dataset.t === 'settings') loadSettings();
        }));

        // ---------------------------------------------------------------- 概览
        async function loadOverview() {
          try {
            const o = await api('/overview');
            $('#s-keys').textContent = o.keys.total;
            $('#s-keys-sub').textContent = `启用 ${o.keys.active} · 禁用 ${o.keys.disabled}`;
            $('#s-tokens').textContent = fmt(o.totals.totalTokens);
            $('#s-tokens-sub').textContent = `输入 ${fmt(o.totals.promptTokens)} · 输出 ${fmt(o.totals.completionTokens)}`;
            $('#s-cost').textContent = money(o.totals.cost);
            $('#s-cost-sub').textContent = o.config.currency;
            $('#s-req').textContent = fmt(o.totals.requests);
            $('#s-req-sub').textContent = `成功 ${fmt(o.totals.successRequests)}`;
            $('#s-today').textContent = fmt(o.today.totalTokens);
            $('#s-today-sub').textContent = `${money(o.today.cost)} · ${o.today.requests} 次请求`;
            window._cfg = o.config;
          } catch (e) { toast('加载概览失败：' + e.message, true); }
        }

        async function checkUpstream() {
          try {
            const h = await api('/ollama/health');
            $('#upstreamState').innerHTML = h.ok
              ? '<span style="color:#7ee787">● Ollama 在线</span>'
              : '<span style="color:#ff9d9d">● Ollama 离线</span>';
          } catch (e) { $('#upstreamState').textContent = ''; }
        }

        // ---------------------------------------------------------------- Keys
        // 把 RPM / 输出速度 / 分钟·天配额渲染成一组小标签
        function rateBadges(q) {
          const parts = [];
          if (q.rateLimitRpm)         parts.push(`RPM ${q.rateLimitRpm}`);
          if (q.maxOutputTokensPerSec) parts.push(`${q.maxOutputTokensPerSec} tok/s`);
          if (q.tokensPerMinute)      parts.push(`${fmt(q.tokensPerMinute)}/分`);
          if (q.tokensPerDay)         parts.push(`${fmt(q.tokensPerDay)}/天`);
          if (parts.length === 0) return '<span class="muted">不限</span>';
          return parts.map(p =>
            `<span class="pill" style="background:#eef3ff;color:#3457c4;font-weight:500">${esc(p)}</span>`
          ).join(' ');
        }

        async function loadKeys() {
          try {
            const keys = await api('/keys');
            const box = $('#keysTable');
            if (!keys.length) { box.innerHTML = '<div class="empty">还没有 API Key，点右上角新建一个。</div>'; return; }

            box.innerHTML = `<table><thead><tr>
              <th>ID</th><th>名称</th><th>Key</th><th>状态</th><th>已用 Token</th>
              <th>Token 配额</th><th>已用金额</th><th>金额上限</th><th>请求数</th>
              <th>限速</th><th>限用模型</th><th>最后使用</th><th class="right">操作</th>
            </tr></thead><tbody>${keys.map(k => {
              const tPct = k.quota.totalTokens ? Math.min(100, k.used.totalTokens / k.quota.totalTokens * 100) : 0;
              const cPct = k.quota.totalCost ? Math.min(100, k.used.cost / k.quota.totalCost * 100) : 0;
              const cls = p => p > 90 ? 'danger' : p > 70 ? 'warn' : '';
              const st = k.status === 'Active' ? '<span class="pill ok">启用</span>'
                       : k.status === 'Disabled' ? '<span class="pill off">禁用</span>'
                       : '<span class="pill exp">过期</span>';
              return `<tr>
                <td>${k.id}</td>
                <td><strong>${esc(k.name)}</strong></td>
                <td><span class="key">${esc(k.keyPrefix)}</span></td>
                <td>${st}</td>
                <td>${fmt(k.used.totalTokens)}</td>
                <td>${k.quota.totalTokens
                      ? `<div class="bar ${cls(tPct)}"><div style="width:${tPct}%"></div></div>
                         <span class="muted">${fmt(k.quota.totalTokens)}</span>`
                      : '<span class="muted">不限</span>'}</td>
                <td>${money(k.used.cost)}</td>
                <td>${k.quota.totalCost
                      ? `<div class="bar ${cls(cPct)}"><div style="width:${cPct}%"></div></div>
                         <span class="muted">${money(k.quota.totalCost)}</span>`
                      : '<span class="muted">不限</span>'}</td>
                <td>${fmt(k.used.requests)}${k.quota.totalRequests ? ' / ' + fmt(k.quota.totalRequests) : ''}</td>
                <td style="white-space:nowrap">${rateBadges(k.quota)}</td>
                <td>${k.allowedModels.length ? esc(k.allowedModels.join(', ')) : '<span class="muted">全部</span>'}</td>
                <td class="muted">${k.lastUsedAt ? new Date(k.lastUsedAt).toLocaleString('zh-CN') : '从未'}</td>
                <td class="right" style="white-space:nowrap">
                  <button class="b-pri" onclick="openEdit(${k.id})">编辑</button>
                  <button class="b-sec" onclick="toggleKey(${k.id},'${k.status}')">${k.status === 'Active' ? '禁用' : '启用'}</button>
                  <button class="b-sec" onclick="resetUsage(${k.id})">清零</button>
                  <button class="b-dan" onclick="delKey(${k.id})">删除</button>
                </td></tr>`;
            }).join('')}</tbody></table>`;
          } catch (e) { toast('加载 Key 失败：' + e.message, true); }
        }

        function openCreate() { $('#m-create').classList.add('show'); }
        function closeCreate() { $('#m-create').classList.remove('show'); }

        const numOrNull = v => (v === '' || v === null || v === undefined) ? null : Number(v);

        async function createKey() {
          try {
            const models = $('#c-models').value.trim();
            const body = {
              name: $('#c-name').value.trim() || '未命名',
              allowedModels: models ? models.split(',').map(s => s.trim()).filter(Boolean) : [],
              quotaTotalTokens: numOrNull($('#c-qtok').value),
              quotaTotalCost: numOrNull($('#c-qcost').value),
              quotaTotalRequests: numOrNull($('#c-qreq').value),
              rateLimitRpm: numOrNull($('#c-rpm').value),
              maxOutputTokensPerSec: numOrNull($('#c-maxout').value),
              quotaTokensPerMinute: numOrNull($('#c-tpm').value),
              quotaTokensPerDay: numOrNull($('#c-tpd').value),
              overrideInputPricePer1K: numOrNull($('#c-oip').value),
              overrideOutputPricePer1K: numOrNull($('#c-oop').value),
              overridePricePerRequest: numOrNull($('#c-opr').value),
              expiresAt: $('#c-exp').value ? new Date($('#c-exp').value).toISOString() : null
            };
            const r = await api('/keys', { method: 'POST', body: JSON.stringify(body) });
            closeCreate();
            $('#showPlain').textContent = r.plainKey;
            $('#m-show').classList.add('show');
            ['c-name','c-qtok','c-qcost','c-qreq','c-rpm','c-maxout','c-tpm','c-tpd',
             'c-exp','c-models','c-oip','c-oop','c-opr']
              .forEach(id => $('#' + id).value = '');
            loadKeys(); loadOverview();
          } catch (e) { toast('创建失败：' + e.message, true); }
        }

        function copyKey() {
          const t = $('#showPlain').textContent;
          navigator.clipboard.writeText(t).then(
            () => toast('已复制到剪贴板'),
            () => toast('复制失败，请手动选中复制', true));
        }

        // ---------------------------------------------------------------- 编辑 Key
        let _editId = null;

        async function openEdit(id) {
          try {
            const keys = await api('/keys');
            const k = keys.find(x => x.id === id);
            if (!k) return toast('未找到该 Key', true);
            _editId = id;

            $('#e-title').textContent = '（' + k.keyPrefix + '）';
            $('#e-name').value = k.name || '';
            $('#e-status').value = k.status;
            // 配额为 null（不限）时留空，符合"留空=不限"的提示
            $('#e-qtok').value = k.quota.totalTokens ?? '';
            $('#e-qcost').value = k.quota.totalCost ?? '';
            $('#e-qreq').value = k.quota.totalRequests ?? '';
            $('#e-rpm').value = k.quota.rateLimitRpm ?? '';
            $('#e-maxout').value = k.quota.maxOutputTokensPerSec ?? '';
            $('#e-tpm').value = k.quota.tokensPerMinute ?? '';
            $('#e-tpd').value = k.quota.tokensPerDay ?? '';
            $('#e-models').value = (k.allowedModels || []).join(', ');
            $('#e-oip').value = k.overrides.inputPricePer1K ?? '';
            $('#e-oop').value = k.overrides.outputPricePer1K ?? '';
            $('#e-opr').value = k.overrides.pricePerRequest ?? '';

            if (k.expiresAt) {
              // datetime-local 需要本地时间的 yyyy-MM-ddTHH:mm
              const d = new Date(k.expiresAt);
              const pad = n => String(n).padStart(2, '0');
              $('#e-exp').value = `${d.getFullYear()}-${pad(d.getMonth()+1)}-${pad(d.getDate())}T${pad(d.getHours())}:${pad(d.getMinutes())}`;
            } else {
              $('#e-exp').value = '';
            }

            $('#e-usage').innerHTML =
              `已累计使用：<strong>${fmt(k.used.totalTokens)}</strong> token（输入 ${fmt(k.used.promptTokens)} / 输出 ${fmt(k.used.completionTokens)}）、` +
              `<strong>${fmt(k.used.requests)}</strong> 次请求、费用 <strong>${money(k.used.cost)}</strong>`;

            $('#m-edit').classList.add('show');
          } catch (e) { toast('读取 Key 失败：' + e.message, true); }
        }

        function closeEdit() { $('#m-edit').classList.remove('show'); _editId = null; }

        async function saveEdit() {
          if (_editId === null) return;
          try {
            const models = $('#e-models').value.trim();
            const body = {
              name: $('#e-name').value.trim() || '未命名',
              status: $('#e-status').value,
              allowedModels: models ? models.split(',').map(s => s.trim()).filter(Boolean) : [],
              quotaTotalTokens: numOrNull($('#e-qtok').value),
              quotaTotalCost: numOrNull($('#e-qcost').value),
              quotaTotalRequests: numOrNull($('#e-qreq').value),
              rateLimitRpm: numOrNull($('#e-rpm').value),
              maxOutputTokensPerSec: numOrNull($('#e-maxout').value),
              quotaTokensPerMinute: numOrNull($('#e-tpm').value),
              quotaTokensPerDay: numOrNull($('#e-tpd').value),
              overrideInputPricePer1K: numOrNull($('#e-oip').value),
              overrideOutputPricePer1K: numOrNull($('#e-oop').value),
              overridePricePerRequest: numOrNull($('#e-opr').value),
              expiresAt: $('#e-exp').value ? new Date($('#e-exp').value).toISOString() : null
            };
            await api('/keys/' + _editId, { method: 'PUT', body: JSON.stringify(body) });
            closeEdit();
            toast('已保存修改');
            loadKeys(); loadOverview();
          } catch (e) { toast('保存失败：' + e.message, true); }
        }

        async function toggleKey(id, status) {
          try {
            await api('/keys/' + id, { method: 'PUT',
              body: JSON.stringify({ status: status === 'Active' ? 'Disabled' : 'Active' }) });
            toast('状态已更新'); loadKeys();
          } catch (e) { toast('操作失败：' + e.message, true); }
        }

        async function resetUsage(id) {
          if (!confirm('确定要把该 Key 的累计用量清零吗？（明细记录会保留）')) return;
          try { await api('/keys/' + id + '/reset-usage', { method: 'POST' }); toast('已清零'); loadKeys(); loadOverview(); }
          catch (e) { toast('操作失败：' + e.message, true); }
        }

        async function delKey(id) {
          if (!confirm('确定删除该 Key？删除后使用该 Key 的客户端将立即失效。')) return;
          try { await api('/keys/' + id, { method: 'DELETE' }); toast('已删除'); loadKeys(); loadOverview(); }
          catch (e) { toast('删除失败：' + e.message, true); }
        }

        // ---------------------------------------------------------------- 定价
        async function loadPrices() {
          try {
            const ps = await api('/prices');
            const box = $('#pricesTable');
            if (!ps.length) { box.innerHTML = '<div class="empty">暂无定价规则。</div>'; return; }
            box.innerHTML = `<table><thead><tr>
              <th>模型</th><th>模式</th><th>输入 / 千 token</th><th>输出 / 千 token</th>
              <th>单次价</th><th>通配</th><th>启用</th><th class="right">操作</th>
            </tr></thead><tbody>${ps.map(p => `<tr>
              <td><span class="key">${esc(p.model)}</span></td>
              <td>${p.mode === 'PerRequest' ? '按次' : '按 Token'}</td>
              <td>${p.inputPricePer1K}</td><td>${p.outputPricePer1K}</td><td>${p.pricePerRequest}</td>
              <td>${p.isWildcard ? '是' : '–'}</td>
              <td>${p.enabled ? '<span class="pill ok">是</span>' : '<span class="pill off">否</span>'}</td>
              <td class="right"><button class="b-dan" onclick="delPrice('${esc(p.model)}')">删除</button></td>
            </tr>`).join('')}</tbody></table>`;
          } catch (e) { toast('加载定价失败：' + e.message, true); }
        }

        async function savePrice() {
          try {
            const body = {
              model: $('#pr-model').value.trim(),
              mode: $('#pr-mode').value,
              inputPricePer1K: Number($('#pr-in').value || 0),
              outputPricePer1K: Number($('#pr-out').value || 0),
              pricePerRequest: Number($('#pr-req').value || 0),
              enabled: true
            };
            if (!body.model) return toast('请填写模型名', true);
            await api('/prices', { method: 'POST', body: JSON.stringify(body) });
            toast('定价已保存'); $('#pr-model').value = ''; loadPrices();
          } catch (e) { toast('保存失败：' + e.message, true); }
        }

        async function delPrice(model) {
          if (!confirm('删除模型 ' + model + ' 的定价规则？')) return;
          try { await api('/prices/' + encodeURIComponent(model), { method: 'DELETE' }); toast('已删除'); loadPrices(); }
          catch (e) { toast('删除失败：' + e.message, true); }
        }

        // ---------------------------------------------------------------- 用量
        async function loadKeyFilter() {
          try {
            const keys = await api('/keys');
            $('#u-key').innerHTML = '<option value="">全部 Key</option>' +
              keys.map(k => `<option value="${k.id}">${esc(k.name)} (${esc(k.keyPrefix)})</option>`).join('');
          } catch (e) { /* 忽略 */ }
        }

        async function loadUsage() {
          try {
            const kid = $('#u-key').value;
            const lim = $('#u-limit').value;
            const rs = await api(`/usage?limit=${lim}${kid ? '&keyId=' + kid : ''}`);
            const box = $('#usageTable');
            if (!rs.length) { box.innerHTML = '<div class="empty">暂无用量记录。</div>'; return; }

            box.innerHTML = `<table><thead><tr>
              <th>时间</th><th>Key</th><th>模型</th><th>模式</th>
              <th>输入</th><th>输出</th><th>估算输出</th><th>偏差</th>
              <th>费用</th><th>结果</th><th>耗时</th><th>首字</th><th>来源 IP</th>
            </tr></thead><tbody>${rs.map(r => {
              const drift = r.tokenDrift || 0;
              const driftHtml = (r.estimatedCompletionTokens === null && r.actualCompletionTokens === null)
                ? '<span class="muted">–</span>'
                : (drift === 0 ? '<span class="pill ok">0</span>'
                   : `<span class="pill ${drift > 0 ? 'err' : 'exp'}">${drift > 0 ? '+' : ''}${drift}</span>`);
              const outPill = r.outcome === 'Success' ? '<span class="pill ok">成功</span>'
                : r.outcome === 'ClientAborted' ? '<span class="pill exp">中断</span>'
                : `<span class="pill err">${esc(r.outcome)}</span>`;
              return `<tr>
                <td class="muted">${new Date(r.createdAt).toLocaleString('zh-CN')}</td>
                <td><span class="key">${esc(r.keyPrefix)}</span></td>
                <td>${esc(r.model || '–')}</td>
                <td class="muted">${r.meteringMode === 'Exact' ? '精确' : '实时'}</td>
                <td>${fmt(r.promptTokens)}</td>
                <td>${fmt(r.completionTokens)}</td>
                <td class="muted">${r.estimatedCompletionTokens === null ? '–' : fmt(r.estimatedCompletionTokens)}</td>
                <td>${driftHtml}</td>
                <td><strong>${money(r.cost)}</strong></td>
                <td>${outPill}</td>
                <td class="muted">${r.durationMs}ms</td>
                <td class="muted">${r.firstTokenMs === null ? '–' : r.firstTokenMs + 'ms'}</td>
                <td class="muted">${esc(r.clientIp)}</td>
              </tr>`;
            }).join('')}</tbody></table>`;
          } catch (e) { toast('加载用量失败：' + e.message, true); }
        }

        // ---------------------------------------------------------------- 统计
        async function loadChart() {
          try {
            const rows = await api('/usage/daily?days=14');
            const box = $('#chart');
            if (!rows.length) { box.innerHTML = '<div class="empty">暂无数据。</div>'; return; }
            const max = Math.max(...rows.map(r => r.promptTokens + r.completionTokens), 1);
            box.innerHTML = `<div class="chart">${rows.map(r => {
              const tot = r.promptTokens + r.completionTokens;
              const h = Math.max(3, tot / max * 118);
              const d = new Date(r.day).toLocaleDateString('zh-CN', { month: 'numeric', day: 'numeric' });
              return `<div class="col" style="height:${h}px" title="${d}：${fmt(tot)} token / ${money(r.cost)}">
                        <span>${d}</span></div>`;
            }).join('')}</div>
            <p class="muted" style="margin-top:24px">合计 ${fmt(rows.reduce((a, r) => a + r.promptTokens + r.completionTokens, 0))} token，
               共 ${money(rows.reduce((a, r) => a + Number(r.cost), 0))}</p>`;
          } catch (e) { toast('加载趋势失败：' + e.message, true); }
        }

        async function loadByModel() {
          try {
            const rows = await api('/usage/by-model?days=30');
            const box = $('#modelTable');
            if (!rows.length) { box.innerHTML = '<div class="empty">暂无数据。</div>'; return; }
            const total = rows.reduce((a, r) => a + Number(r.cost), 0) || 1;
            box.innerHTML = `<table><thead><tr>
              <th>模型</th><th>请求数</th><th>输入 Token</th><th>输出 Token</th>
              <th>费用</th><th>费用占比</th></tr></thead><tbody>${rows.map(r => {
              const pct = Number(r.cost) / total * 100;
              return `<tr>
                <td><span class="key">${esc(r.model || '(未知)')}</span></td>
                <td>${fmt(r.requests)}</td><td>${fmt(r.promptTokens)}</td><td>${fmt(r.completionTokens)}</td>
                <td><strong>${money(r.cost)}</strong></td>
                <td><div class="bar"><div style="width:${pct.toFixed(1)}%"></div></div>
                    <span class="muted">${pct.toFixed(1)}%</span></td>
              </tr>`;
            }).join('')}</tbody></table>`;
          } catch (e) { toast('加载模型分布失败：' + e.message, true); }
        }

        // ---------------------------------------------------------------- 校准
        async function loadCalib() {
          try {
            const c = await api('/calibration?days=7');
            $('#calibCards').innerHTML = `
              <div class="card"><div class="lbl">样本数</div><div class="val">${fmt(c.sampleCount)}</div>
                <div class="sub">近 ${c.days} 天同时有估算与实测的请求</div></div>
              <div class="card"><div class="lbl">平均估算输出</div><div class="val">${c.avgEstimated}</div>
                <div class="sub">token / 次</div></div>
              <div class="card"><div class="lbl">平均实测输出</div><div class="val">${c.avgActual}</div>
                <div class="sub">token / 次</div></div>
              <div class="card"><div class="lbl">实测 / 估算 比值</div>
                <div class="val" style="color:${c.ratio > 1.05 ? '#c0392b' : c.ratio < 0.95 ? '#b26a00' : '#1a7f4b'}">
                  ${c.ratio}</div><div class="sub">理想值 1.0</div></div>`;
            $('#calibHint').innerHTML = `<div class="warnbox">${esc(c.hint)}
              ${c.sampleCount > 0 ? `<br><br>建议把 <code>Billing:CharsPerToken</code> 调整为
              当前值 × ${c.suggestedCharsPerTokenFactor}</span>` : ''}</div>`;
          } catch (e) { toast('加载校准数据失败：' + e.message, true); }
        }

        // ---------------------------------------------------------------- 账号安全
        async function loadAccount() {
          try {
            const a = await api('/account');
            $('#acc-current').textContent = a.username;
            $('#acc-hint').textContent = a.hint;
          } catch (e) {
            $('#acc-hint').textContent = '读取账号信息失败：' + e.message;
          }
        }

        function clearAccountForm() {
          ['acc-curpass','acc-newuser','acc-newpass','acc-confirm'].forEach(id => $('#' + id).value = '');
          $('#acc-msg').style.display = 'none';
        }

        function accMsg(text, ok) {
          const el = $('#acc-msg');
          el.style.display = 'block';
          el.innerHTML = `<div class="warnbox" style="${ok
            ? 'background:#e7f8ee;border-left-color:#1a7f4b'
            : 'background:#ffeaea;border-left-color:#c0392b'}">${esc(text)}</div>`;
        }

        async function saveAccount() {
          const curpass = $('#acc-curpass').value;
          const newuser = $('#acc-newuser').value.trim();
          const newpass = $('#acc-newpass').value;
          const confirm = $('#acc-confirm').value;

          if (!curpass) return accMsg('请输入当前密码以确认身份。', false);
          if (!newuser && !newpass) return accMsg('用户名和新密码至少要填一项。', false);
          if (newpass && newpass.length < 8) return accMsg('新密码至少 8 位。', false);
          if (newpass !== confirm) return accMsg('两次输入的新密码不一致。', false);

          if (!confirm('保存后当前登录会失效，需要用新凭据重新登录。确定继续？')) return;

          try {
            const r = await api('/account', { method: 'POST', body: JSON.stringify({
              currentPassword: curpass,
              newUsername: newuser || null,
              newPassword: newpass || null,
              confirmPassword: confirm || null
            }) });
            accMsg(r.message || '账号已更新。', true);
            toast('账号已更新，正在跳转登录页…');
            setTimeout(() => location.href = '/admin/login', 1600);
          } catch (e) {
            accMsg('修改失败：' + e.message, false);
          }
        }

        // ---------------------------------------------------------------- 设置
        async function loadSettings() {
          try {
            const o = await api('/overview');
            const c = o.config;
            $('#settingsView').innerHTML = `<table><tbody>
              <tr><th style="width:220px">Ollama 上游地址</th><td><span class="key">${esc(c.ollamaBaseUrl)}</span></td></tr>
              <tr><th>计量模式</th><td><strong>${esc(c.meteringMode)}</strong>
                <span class="muted">（${c.meteringMode === 'Exact' ? '缓冲转发，token 精确，首字延迟较高'
                  : '边流边发，首字快，用估算值计费'}）</span></td></tr>
              <tr><th>币种</th><td>${esc(c.currency)}</td></tr>
              <tr><th>估算参数 CharsPerToken</th><td>${c.charsPerToken}</td></tr>
              <tr><th>记录用量明细</th><td>${c.logUsageRecords ? '是' : '否'}</td></tr>
              <tr><th>启用校准采集</th><td>${c.calibration ? '是' : '否'}</td></tr>
              <tr><th>落库失败丢弃数</th><td>${fmt(o.health.droppedRecords)}</td></tr>
            </tbody></table>
            <p class="muted" style="margin-top:15px">以上配置在 <code>appsettings.json</code> 的
               <code>Billing</code> 节中修改，重启网关后生效。</p>`;
          } catch (e) { toast('加载设置失败：' + e.message, true); }
        }

        // ---------------------------------------------------------------- 启动
        loadOverview(); loadKeys(); loadKeyFilter(); checkUpstream(); loadPrices();
        setInterval(() => { loadOverview(); checkUpstream(); }, 20000);
        setInterval(() => { if ($('#p-keys').classList.contains('active')) loadKeys(); }, 15000);
        </script></body></html>
        """;
}
