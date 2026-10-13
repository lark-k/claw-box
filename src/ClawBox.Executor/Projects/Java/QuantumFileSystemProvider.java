package quantum.runtime;

import java.io.*;
import java.net.URI;
import java.nio.channels.*;
import java.nio.file.*;
import java.nio.file.attribute.*;
import java.nio.file.spi.FileSystemProvider;
import java.util.*;
import java.util.concurrent.ExecutorService;

/** AppContainer's DOS-volume lookup is denied even for readable files. All I/O
 * still goes through the Windows provider; only realpath has a bounded fallback. */
public final class QuantumFileSystemProvider extends FileSystemProvider {
    private final FileSystemProvider backend;
    private final WrappedFileSystem filesystem;
    public QuantumFileSystemProvider(FileSystemProvider backend) {
        this.backend = backend;
        this.filesystem = new WrappedFileSystem(backend.getFileSystem(URI.create("file:///")));
    }
    private Path unwrap(Path path) { return path instanceof WrappedPath p ? p.path : path; }
    private Path wrap(Path path) { return path == null ? null : new WrappedPath(path); }
    @Override public String getScheme() { return backend.getScheme(); }
    @Override public FileSystem newFileSystem(URI uri, Map<String, ?> env) throws IOException { return backend.newFileSystem(uri, env); }
    @Override public FileSystem getFileSystem(URI uri) { return filesystem; }
    @Override public Path getPath(URI uri) { return wrap(backend.getPath(uri)); }
    @Override public SeekableByteChannel newByteChannel(Path path, Set<? extends OpenOption> options, FileAttribute<?>... attrs) throws IOException { return backend.newByteChannel(unwrap(path), options, attrs); }
    @Override public FileChannel newFileChannel(Path path, Set<? extends OpenOption> options, FileAttribute<?>... attrs) throws IOException { return backend.newFileChannel(unwrap(path), options, attrs); }
    @Override public AsynchronousFileChannel newAsynchronousFileChannel(Path path, Set<? extends OpenOption> options, ExecutorService executor, FileAttribute<?>... attrs) throws IOException { return backend.newAsynchronousFileChannel(unwrap(path), options, executor, attrs); }
    @Override public DirectoryStream<Path> newDirectoryStream(Path dir, DirectoryStream.Filter<? super Path> filter) throws IOException {
        DirectoryStream<Path> stream = backend.newDirectoryStream(unwrap(dir), p -> filter.accept(wrap(p)));
        return new DirectoryStream<>() {
            public Iterator<Path> iterator() { Iterator<Path> iterator = stream.iterator(); return new Iterator<>() { public boolean hasNext() { return iterator.hasNext(); } public Path next() { return wrap(iterator.next()); } }; }
            public void close() throws IOException { stream.close(); }
        };
    }
    @Override public void createDirectory(Path dir, FileAttribute<?>... attrs) throws IOException { backend.createDirectory(unwrap(dir), attrs); }
    @Override public void delete(Path path) throws IOException { backend.delete(unwrap(path)); }
    @Override public void copy(Path source, Path target, CopyOption... options) throws IOException { backend.copy(unwrap(source), unwrap(target), options); }
    @Override public void move(Path source, Path target, CopyOption... options) throws IOException { backend.move(unwrap(source), unwrap(target), options); }
    @Override public boolean isSameFile(Path first, Path second) throws IOException {
        try { return backend.isSameFile(unwrap(first), unwrap(second)); }
        catch (AccessDeniedException error) { return first.toRealPath().equals(second.toRealPath()); }
    }
    @Override public boolean isHidden(Path path) throws IOException { return backend.isHidden(unwrap(path)); }
    @Override public FileStore getFileStore(Path path) throws IOException { return backend.getFileStore(unwrap(path)); }
    @Override public void checkAccess(Path path, AccessMode... modes) throws IOException { backend.checkAccess(unwrap(path), modes); }
    @Override public <V extends FileAttributeView> V getFileAttributeView(Path path, Class<V> type, LinkOption... options) { return backend.getFileAttributeView(unwrap(path), type, options); }
    @Override public <A extends BasicFileAttributes> A readAttributes(Path path, Class<A> type, LinkOption... options) throws IOException { return backend.readAttributes(unwrap(path), type, options); }
    @Override public Map<String, Object> readAttributes(Path path, String attributes, LinkOption... options) throws IOException { return backend.readAttributes(unwrap(path), attributes, options); }
    @Override public void setAttribute(Path path, String attribute, Object value, LinkOption... options) throws IOException { backend.setAttribute(unwrap(path), attribute, value, options); }
    @Override public void createSymbolicLink(Path link, Path target, FileAttribute<?>... attrs) throws IOException { backend.createSymbolicLink(unwrap(link), unwrap(target), attrs); }
    @Override public void createLink(Path link, Path existing) throws IOException { backend.createLink(unwrap(link), unwrap(existing)); }
    @Override public Path readSymbolicLink(Path link) throws IOException { return wrap(backend.readSymbolicLink(unwrap(link))); }

    private final class WrappedFileSystem extends FileSystem {
        private final FileSystem delegate;
        private WrappedFileSystem(FileSystem delegate) { this.delegate = delegate; }
        @Override public FileSystemProvider provider() { return QuantumFileSystemProvider.this; }
        @Override public void close() throws IOException { delegate.close(); }
        @Override public boolean isOpen() { return delegate.isOpen(); }
        @Override public boolean isReadOnly() { return delegate.isReadOnly(); }
        @Override public String getSeparator() { return delegate.getSeparator(); }
        @Override public Iterable<Path> getRootDirectories() { List<Path> roots = new ArrayList<>(); delegate.getRootDirectories().forEach(p -> roots.add(wrap(p))); return roots; }
        @Override public Iterable<FileStore> getFileStores() { return delegate.getFileStores(); }
        @Override public Set<String> supportedFileAttributeViews() { return delegate.supportedFileAttributeViews(); }
        @Override public Path getPath(String first, String... more) { return wrap(delegate.getPath(first, more)); }
        @Override public PathMatcher getPathMatcher(String syntax) { PathMatcher matcher = delegate.getPathMatcher(syntax); return p -> matcher.matches(unwrap(p)); }
        @Override public UserPrincipalLookupService getUserPrincipalLookupService() { return delegate.getUserPrincipalLookupService(); }
        @Override public WatchService newWatchService() throws IOException { return delegate.newWatchService(); }
    }
    private final class WrappedPath implements Path {
        private final Path path;
        private WrappedPath(Path path) { this.path = path; }
        @Override public FileSystem getFileSystem() { return filesystem; }
        @Override public boolean isAbsolute() { return path.isAbsolute(); }
        @Override public Path getRoot() { return wrap(path.getRoot()); }
        @Override public Path getFileName() { return wrap(path.getFileName()); }
        @Override public Path getParent() { return wrap(path.getParent()); }
        @Override public int getNameCount() { return path.getNameCount(); }
        @Override public Path getName(int index) { return wrap(path.getName(index)); }
        @Override public Path subpath(int begin, int end) { return wrap(path.subpath(begin, end)); }
        @Override public boolean startsWith(Path other) { return path.startsWith(unwrap(other)); }
        @Override public boolean startsWith(String other) { return path.startsWith(other); }
        @Override public boolean endsWith(Path other) { return path.endsWith(unwrap(other)); }
        @Override public boolean endsWith(String other) { return path.endsWith(other); }
        @Override public Path normalize() { return wrap(path.normalize()); }
        @Override public Path resolve(Path other) { return wrap(path.resolve(unwrap(other))); }
        @Override public Path resolve(String other) { return wrap(path.resolve(other)); }
        @Override public Path resolveSibling(Path other) { return wrap(path.resolveSibling(unwrap(other))); }
        @Override public Path resolveSibling(String other) { return wrap(path.resolveSibling(other)); }
        @Override public Path relativize(Path other) { return wrap(path.relativize(unwrap(other))); }
        @Override public URI toUri() { return path.toUri(); }
        @Override public Path toAbsolutePath() { return wrap(path.toAbsolutePath()); }
        @Override public Path toRealPath(LinkOption... options) throws IOException {
            try { return wrap(path.toRealPath(options)); }
            catch (AccessDeniedException denied) {
                Path absolute = path.toAbsolutePath().normalize();
                for (String allowed : System.getenv().getOrDefault("QUANTUM_REALPATH_ROOTS", "").split(";")) {
                    if (allowed.isEmpty()) continue;
                    Path root = filesystem.delegate.getPath(allowed).toAbsolutePath().normalize();
                    if (!absolute.startsWith(root)) continue;
                    for (Path current = absolute; current != null; current = current.getParent()) {
                        BasicFileAttributes attributes = backend.readAttributes(current, BasicFileAttributes.class, LinkOption.NOFOLLOW_LINKS);
                        if (attributes.isSymbolicLink() || attributes.isOther()) throw denied;
                        if (current.equals(root)) return wrap(absolute);
                    }
                }
                throw denied;
            }
        }
        @Override public File toFile() { return new File(path.toString()); }
        @Override public WatchKey register(WatchService watcher, WatchEvent.Kind<?>[] events, WatchEvent.Modifier... modifiers) throws IOException { return path.register(watcher, events, modifiers); }
        @Override public Iterator<Path> iterator() { Iterator<Path> iterator = path.iterator(); return new Iterator<>() { public boolean hasNext() { return iterator.hasNext(); } public Path next() { return wrap(iterator.next()); } }; }
        @Override public int compareTo(Path other) { return path.compareTo(unwrap(other)); }
        @Override public boolean equals(Object other) { return other instanceof QuantumFileSystemProvider.WrappedPath p && path.equals(p.path); }
        @Override public int hashCode() { return path.hashCode(); }
        @Override public String toString() { return path.toString(); }
    }
}
