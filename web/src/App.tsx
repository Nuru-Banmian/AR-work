import { useEffect, useState } from 'react'
import { listIssues, serverAddress, updateStatus, type Issue } from './api'
import './App.css'

const priorityNames = { low: '低', medium: '中', high: '高' }
const statusNames = { open: '待处理', in_progress: '处理中', resolved: '已解决' }

function IssueCard({ initialIssue, address }: { initialIssue: Issue; address: string }) {
  const [issue, setIssue] = useState(initialIssue)
  const [saving, setSaving] = useState(false)
  const [message, setMessage] = useState('')
  const [error, setError] = useState('')

  async function changeStatus(status: Issue['status']) {
    if (saving || status === issue.status) return
    setSaving(true)
    setError('')
    setMessage('')
    try {
      setIssue(await updateStatus(address, issue.id, status))
      setMessage('状态已保存')
    } catch (reason) {
      setError(`保存失败，仍显示原状态：${reason instanceof Error ? reason.message : '请检查网络'}。可重试或刷新确认。`)
    } finally { setSaving(false) }
  }

  return <article>
    <div className="row"><span className={`priority ${issue.priority}`}>{priorityNames[issue.priority]}优先级</span><span>{statusNames[issue.status]} · {issue.status}</span></div>
    <h2>{issue.title}</h2><p className="id">ID: {issue.id}</p>
    <p>{issue.description || '未填写描述'}</p>
    <dl><dt>标记位置（米）</dt><dd>x: {issue.position.x}, y: {issue.position.y}, z: {issue.position.z}</dd><dt>创建时间</dt><dd>{new Date(issue.createdAt).toLocaleString()}</dd><dt>更新时间</dt><dd>{new Date(issue.updatedAt).toLocaleString()}</dd></dl>
    <label>处理状态 <select aria-label={`${issue.title} 处理状态`} value={issue.status} disabled={saving} onChange={event => { void changeStatus(event.target.value as Issue['status']) }}>
      <option value="open">待处理</option><option value="in_progress">处理中</option><option value="resolved">已解决</option>
    </select></label>
    {saving && <p role="status">正在保存状态…</p>}
    {message && <p role="status">{message}</p>}
    {error && <p role="alert">{error}</p>}
  </article>
}

export default function App() {
  const [address, setAddress] = useState(() => localStorage.getItem('arwork.server') || 'http://localhost:8080')
  const [activeAddress, setActiveAddress] = useState(address)
  const [revision, setRevision] = useState(0)
  const [issues, setIssues] = useState<Issue[]>([])
  const [loading, setLoading] = useState(true)
  const [error, setError] = useState('')

  useEffect(() => {
    const controller = new AbortController()
    let active = true
    const timeout = setTimeout(() => controller.abort(), 10000)
    listIssues(activeAddress, controller.signal)
      .then(data => { if (active) setIssues(data) })
      .catch((reason: unknown) => { if (active) setError(`无法加载问题：${reason instanceof Error ? reason.message : '请检查服务地址和网络'}`) })
      .finally(() => { clearTimeout(timeout); if (active) setLoading(false) })
    return () => { active = false; clearTimeout(timeout); controller.abort() }
  }, [activeAddress, revision])

  return <main>
    <header><p className="eyebrow">AR WORK · 园区巡检</p><h1>现场问题</h1><p>查看手机上报的问题。位置以当前 AR 会话的米制坐标记录。</p></header>
    <form className="connection" onSubmit={event => {
      event.preventDefault()
      try {
        const normalized = serverAddress(address)
        localStorage.setItem('arwork.server', normalized)
        setLoading(true)
        setError('')
        setActiveAddress(normalized)
        setRevision(value => value + 1)
      } catch (reason) { setError(reason instanceof Error ? reason.message : '服务地址无效') }
    }}>
      <label htmlFor="server">后端地址</label>
      <input id="server" value={address} onChange={event => setAddress(event.target.value)} required />
      <button disabled={loading}>{loading ? '加载中…' : '连接并刷新'}</button>
    </form>
    {error && <section role="alert"><strong>{error}</strong><p>确认 Go 服务已启动、地址正确且允许浏览器跨域访问。</p><button onClick={() => { setLoading(true); setError(''); setRevision(value => value + 1) }} disabled={loading}>重试</button></section>}
    {loading && <p role="status">正在加载问题…</p>}
    {!loading && !error && issues.length === 0 && <p className="empty">暂无问题。请先在手机上选点并提交，再点击刷新。</p>}
    {!loading && !error && <section aria-label="问题列表" className="issues">{issues.map(issue => <IssueCard key={`${activeAddress}/${issue.id}/${issue.updatedAt}`} initialIssue={issue} address={activeAddress} />)}</section>}
  </main>
}
