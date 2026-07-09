import React, { createContext, useContext, useState, useCallback } from 'react';

interface UIContextType {
    onAddAction: (() => void) | null;
    registerAddAction: (action: (() => void) | null) => void;
    searchTerm: string;
    setSearchTerm: (term: string) => void;
    sidebarOpen: boolean;
    toggleSidebar: () => void;
}

const UIContext = createContext<UIContextType | undefined>(undefined);

export const UIProvider: React.FC<{ children: React.ReactNode }> = ({ children }) => {
    const [onAddAction, setOnAddAction] = useState<(() => void) | null>(null);
    const [searchTerm, setSearchTerm] = useState('');
    const [sidebarOpen, setSidebarOpen] = useState(true);

    const registerAddAction = useCallback((action: (() => void) | null) => {
        setOnAddAction(() => action);
    }, []);

    const toggleSidebar = useCallback(() => {
        setSidebarOpen(prev => !prev);
    }, []);

    return (
        <UIContext.Provider value={{
            onAddAction,
            registerAddAction,
            searchTerm,
            setSearchTerm,
            sidebarOpen,
            toggleSidebar
        }}>
            {children}
        </UIContext.Provider>
    );
};

export const useUI = () => {
    const context = useContext(UIContext);
    if (context === undefined) {
        throw new Error('useUI must be used within a UIProvider');
    }
    return context;
};
